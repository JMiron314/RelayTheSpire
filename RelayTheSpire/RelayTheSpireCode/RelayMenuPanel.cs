using Godot;
using MegaCrit.Sts2.Core.Logging;
using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using System.IO;

namespace RelayTheSpire.UI;

/// <summary>
/// A simple overlay panel injected into the main menu.
/// Lets players enter a run ID to pull a relay bundle from GitHub.
///
/// Layout:
///   ┌─────────────────────────────────┐
///   │  🔗 RelayTheSpire               │
///   │                                 │
///   │  Run ID: [__________________]   │
///   │                                 │
///   │         [Receive Turn]          │
///   │                                 │
///   │  Status: Waiting...             │
///   └─────────────────────────────────┘
///
/// After a successful pull, the player just clicks "Continue" in the normal
/// game UI — the save file is already on disk.
/// </summary>
public partial class RelayMenuPanel : PanelContainer
{
    private LineEdit  _runIdInput  = null!;
    private Button    _batonButton = null!;
    private Button    _pullButton  = null!;
    private Button    _startButton = null!;
    private Label     _statusLabel = null!;

    private static void LogSaveDiagnostics(string label)
    {
        var path   = RelaySavePaths.GetFilePath("current_run.save");
        var exists = File.Exists(path);

        Log.Info($"[RelayTheSpire] [{label}] " +
                 $"HasRunSave={SaveManager.Instance.HasRunSave}, " +
                 $"path={path}, " +
                 $"File.Exists={exists}, " +
                 $"size={(exists ? new FileInfo(path).Length : -1)}");
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(400, 0);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_top",    16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        margin.AddThemeConstantOverride("margin_left",   20);
        margin.AddThemeConstantOverride("margin_right",  20);
        AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        // Title
        var title = new Label { Text = "RelayTheSpire" };
        title.AddThemeFontSizeOverride("font_size", 20);
        vbox.AddChild(title);

        vbox.AddChild(new HSeparator());

        // Take the Baton: one click, picks the best run for this player.
        _batonButton = new Button
        {
            Text        = "Take the Baton",
            TooltipText = "Finds the furthest-along relay run that you haven't played yet " +
                          "and downloads it.",
        };
        _batonButton.Pressed += OnBatonPressed;
        vbox.AddChild(_batonButton);

        var orLabel = new Label { Text = "or enter a specific run:" };
        orLabel.AddThemeFontSizeOverride("font_size", 13);
        orLabel.Modulate = new Color(0.8f, 0.8f, 0.8f);
        vbox.AddChild(orLabel);

        // Run ID row
        var runIdRow = new HBoxContainer();
        vbox.AddChild(runIdRow);

        var runIdLabel = new Label { Text = "Run ID:" };
        runIdLabel.CustomMinimumSize = new Vector2(70, 0);
        runIdRow.AddChild(runIdLabel);

        _runIdInput = new LineEdit
        {
            PlaceholderText     = "e.g. JWH64KK3BX",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _runIdInput.TextSubmitted += _ => OnPullPressed();
        runIdRow.AddChild(_runIdInput);

        // Pull button
        _pullButton = new Button { Text = "Receive Turn" };
        _pullButton.Pressed += OnPullPressed;
        vbox.AddChild(_pullButton);

        vbox.AddChild(new HSeparator());

        // Start button: turns the current in-progress run into a relay run.
        _startButton = new Button
        {
            Text        = "Start Relay From Current Run",
            TooltipText = "Marks your current in-progress run as a relay run. " +
                          "It will be pushed when you finish your floor(s).",
        };
        _startButton.Pressed += OnStartPressed;
        vbox.AddChild(_startButton);

        // Status label
        _statusLabel = new Label
        {
            Text         = "Enter a run ID to receive a relay turn.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        vbox.AddChild(_statusLabel);

        // Shown once after a forced exit to the menu following a successful push.
        if (RelayTheSpireMod.PendingMenuMessage is { } pending)
        {
            SetStatus(pending, isError: false);
            RelayTheSpireMod.PendingMenuMessage = null;
        }
    }

    // -------------------------------------------------------------------------
    // Pull logic
    // -------------------------------------------------------------------------

    // Returns null on success, otherwise a human-readable reason for the failure.
    private string? RefreshMainMenu()
    {
        Node? node = GetParent();
        while (node != null && node is not NMainMenu)
            node = node.GetParent();

        if (node is not NMainMenu mainMenu)
            return "couldn't find the main menu node in the scene tree.";

        if (!SaveManager.Instance.HasRunSave)
        {
            var path = RelaySavePaths.GetFilePath("current_run.save");
            return $"the game still doesn't see a run save at {path}.";
        }

        try
        {
            mainMenu.RefreshButtons();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"[RelayTheSpire] RefreshButtons failed: {ex}");
            return $"RefreshButtons threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void OnPullPressed()
    {
        var runId = _runIdInput.Text.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(runId))
        {
            SetStatus("Please enter a run ID.", isError: true);
            return;
        }

        if (RelayTheSpireMod.Controller is null)
        {
            SetStatus("RelayTheSpire.cfg not configured. Check your mod folder.", isError: true);
            return;
        }

        _ = DoPull(runId);
    }

    // Backups live in a sibling folder, NOT in the saves folder. The game's cloud sync
    // (CloudSaveStore.SyncCloudToLocal) deletes any local file in a synced folder that doesn't
    // exist in Steam Cloud, which would destroy the very backups meant to protect the player's run.
    private static string BackupDirectory(string saveDir) =>
        Path.Combine(Directory.GetParent(saveDir)!.FullName, "relay_backups");

    // Copies the existing run (and manifest) to timestamped .bak files.
    // Returns the backup path, or null if there was no existing run.
    private static string? BackupExistingRun(string saveDir)
    {
        var savePath = Path.Combine(saveDir, "current_run.save");
        if (!File.Exists(savePath))
            return null;

        var backupDir = BackupDirectory(saveDir);
        Directory.CreateDirectory(backupDir);

        var stamp      = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(backupDir, $"current_run.save.bak-{stamp}");
        File.Copy(savePath, backupPath, overwrite: false);

        var manifestPath = Path.Combine(saveDir, "relay_manifest.json");
        if (File.Exists(manifestPath))
            File.Copy(manifestPath, Path.Combine(backupDir, $"relay_manifest.json.bak-{stamp}"), overwrite: false);

        PruneBackups(backupDir, "current_run.save.bak-*", keep: 5);
        PruneBackups(backupDir, "relay_manifest.json.bak-*", keep: 5);
        return backupPath;
    }

    private static void PruneBackups(string dir, string pattern, int keep)
    {
        try
        {
            var old = new DirectoryInfo(dir).GetFiles(pattern);
            Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = keep; i < old.Length; i++)
                old[i].Delete();
        }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] Backup pruning failed: {ex.Message}");
        }
    }

    private void OnStartPressed()
    {
        if (RelayTheSpireMod.Controller is null)
        {
            SetStatus("RelayTheSpire.cfg not configured. Check your mod folder.", isError: true);
            return;
        }

        _ = DoStart();
    }

    private async Task DoStart()
    {
        SetStatus("Checking your current run...", isError: false);
        SetBusy(true);

        try
        {
            var result = await RelayTheSpireMod.Controller!.StartRelayAsync();

            if (!result.Success)
            {
                SetStatus($"Error: {result.Error}", isError: true);
                Log.Warn($"[RelayTheSpire] Start relay failed: {result.Error}");
                return;
            }

            var runId = result.Manifest!.RunId;
            DisplayServer.ClipboardSet(runId);

            var headline = result.AlreadyStarted
                ? "This run is already a relay run."
                : "Relay started!";

            SetStatus(
                $"{headline} Run ID: {runId} (copied to clipboard).\n" +
                "Click 'Continue', play your floor, then pick the next map node." +
                "Then the run is handed off to the next player. ",
                isError: false);

            Log.Info($"[RelayTheSpire] Relay {(result.AlreadyStarted ? "already active" : "started")}. " +
                     $"Run {runId}, floor {result.Save?.VisitedMapCoordsCount}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Unexpected error: {ex.Message}", isError: true);
            Log.Error($"[RelayTheSpire] Start relay exception: {ex}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnBatonPressed()
    {
        if (RelayTheSpireMod.Controller is null)
        {
            SetStatus("RelayTheSpire.cfg not configured. Check your mod folder.", isError: true);
            return;
        }

        _ = DoTakeBaton();
    }

    private async Task DoTakeBaton()
    {
        SetStatus("Looking for a run that needs a player...", isError: false);
        SetBusy(true);

        try
        {
            var playerHash = RelayTheSpireMod.GetLocalPlayerHash();
            var search     = await RelayTheSpireMod.Controller!.FindBatonAsync(playerHash);

            if (!search.Success)
            {
                SetStatus($"Error: {search.Error}", isError: true);
                Log.Warn($"[RelayTheSpire] Baton search failed: {search.Error}");
                return;
            }

            if (search.RunId is null)
            {
                string message;
                if (search.TotalRuns == 0)
                {
                    message = "There are no relay runs yet.";
                }
                else
                {
                    var ongoing  = search.OngoingRuns;
                    var finished = search.FinishedRuns;

                    message = $"There {(ongoing == 1 ? "is" : "are")} {ongoing} ongoing " +
                              $"run{(ongoing == 1 ? "" : "s")} and {finished} finished " +
                              $"run{(finished == 1 ? "" : "s")}.";

                    message += ongoing == 0
                        ? " Nothing is in progress, so there's no run to take."
                        : $" {(ongoing == 1 ? "It isn't" : "None of the ongoing runs are")} available to you right now " +
                          "(you're blocked by the repeat rule, or another player is currently playing it).";
                }

                SetStatus(message, isError: false);
                ShowPopup(message + "\n\nNo run is waiting for you right now.");
                Log.Info($"[RelayTheSpire] Baton search: no available run ({search.TotalRuns} total, " +
                         $"{search.PlayedByYou} already played by this player).");
                return;
            }

            var manifest = search.Manifest!;

            // Don't clobber a turn that's already in progress: if the run we'd download is the
            // one already sitting in the player's save slot, just point them at Continue.
            if (IsHoldingRun(manifest))
            {
                SetStatus(
                    $"You're already holding run {search.RunId} (floor {manifest.FloorsCompleted}). " +
                    "Click 'Continue' to play your turn.",
                    isError: false);
                return;
            }

            _runIdInput.Text = search.RunId;
            SetStatus($"Found run {search.RunId} at floor {manifest.FloorsCompleted}. Downloading...", isError: false);
            Log.Info($"[RelayTheSpire] Baton search picked run {search.RunId} (floor {manifest.FloorsCompleted}, " +
                     $"{search.TotalRuns - search.PlayedByYou} of {search.TotalRuns} runs available).");

            await DoPull(search.RunId);
        }
        catch (Exception ex)
        {
            SetStatus($"Unexpected error: {ex.Message}", isError: true);
            Log.Error($"[RelayTheSpire] Take the Baton exception: {ex}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // True if the player's current save slot already holds the given relay run.
    private static bool IsHoldingRun(RelayManifest candidate)
    {
        try
        {
            var manifestPath = RelaySavePaths.GetFilePath(RelayManifest.FileName);
            var savePath     = RelaySavePaths.GetFilePath("current_run.save");
            if (!File.Exists(manifestPath) || !File.Exists(savePath))
                return false;

            var local = RelayManifest.FromJson(File.ReadAllText(manifestPath));
            return local != null && candidate.StartTime != 0 && local.StartTime == candidate.StartTime;
        }
        catch
        {
            return false;
        }
    }

    private void ShowPopup(string message)
    {
        var dialog = new AcceptDialog { Title = "RelayTheSpire", DialogText = message };
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled  += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }

    private void SetBusy(bool busy)
    {
        _batonButton.Disabled = busy;
        _pullButton.Disabled  = busy;
        _startButton.Disabled = busy;
    }

    private async Task DoPull(string runId)
    {
        SetStatus("Downloading run save...", isError: false);
        SetBusy(true);

        try
        {
            var playerHash = RelayTheSpireMod.GetLocalPlayerHash();
            var saveDir    = RelaySavePaths.GetSaveDirectory();

            // Back up any in-progress run BEFORE the pull can overwrite it.
            // If the backup fails, abort rather than risk losing the run.
            string? backupPath;
            try
            {
                backupPath = BackupExistingRun(saveDir);
                if (backupPath != null)
                    Log.Info($"[RelayTheSpire] Backed up existing run to {backupPath}");
            }
            catch (Exception ex)
            {
                SetStatus($"Couldn't back up your current run, so nothing was changed: {ex.Message}", isError: true);
                Log.Error($"[RelayTheSpire] Backup failed, pull aborted: {ex}");
                return;
            }

            LogSaveDiagnostics("before pull");

            var result = await RelayTheSpireMod.Controller!.PullRelayAsync(runId, playerHash);

            LogSaveDiagnostics("after pull");

            if (result.Success)
            {
                var floor = result.Save?.VisitedMapCoordsCount ?? -1;
                var seed  = result.Save?.Seed ?? runId;

                var refreshError = RefreshMainMenu();
                var backupNote   = backupPath != null
                    ? $"\nYour previous run was backed up as {Path.GetFileName(backupPath)} (in the relay_backups folder next to your saves folder)."
                    : "";

                if (refreshError == null)
                {
                    SetStatus(
                        $"Run received! Floor {floor}, seed {seed}.\n" +
                        "Click 'Continue' in the main menu to start your turn." + backupNote,
                        isError: false);
                }
                else
                {
                    SetStatus(
                        $"Run saved (floor {floor}, seed {seed}), but the menu didn't refresh: {refreshError}" + backupNote,
                        isError: true);
                    Log.Warn($"[RelayTheSpire] Menu refresh failed: {refreshError}");
                }

                Log.Info($"[RelayTheSpire] Pull successful. Run {seed}, floor {floor}.");
            }
            else
            {
                SetStatus($"Error: {result.Error}", isError: true);
                Log.Warn($"[RelayTheSpire] Pull failed: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Unexpected error: {ex.Message}", isError: true);
            Log.Error($"[RelayTheSpire] Pull exception: {ex}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        _statusLabel.Text     = message;
        _statusLabel.Modulate = isError
            ? new Color(1f, 0.4f, 0.4f)
            : new Color(0.7f, 1f, 0.7f);
    }
}