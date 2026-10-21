using Godot;
using MegaCrit.Sts2.Core.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace RelayTheSpire.UI;

/// <summary>
/// The "My Runs" tab of the main-menu panel: every relay run the player has taken a turn in, newest first,
/// built from the local history (RelayLocalStore.MyRuns). It reads only the local file and makes no network
/// requests, so it says what the player did and how their last turn ended, not where the run stands now.
/// RelayMenuPanel calls <see cref="Refresh"/> when the tab is opened.
///
/// The Refresh button is the only thing that touches the network: it fetches the manifest of each run you
/// haven't seen end (one request each, the 30 most recent) and adds a "Now:" line saying whose turn it is.
/// The results are kept in memory, so they survive the panel being rebuilt, and are shown with the time
/// they were fetched, because they go stale.
/// </summary>
public partial class RelayMyRunsView : VBoxContainer
{
    private static readonly Color WonColor   = new(0.7f, 1f, 0.7f);
    private static readonly Color LostColor  = new(1f, 0.5f, 0.5f);
    private static readonly Color MutedColor = new(0.8f, 0.8f, 0.8f);

    private const int MaxLiveRuns = 30;

    private static readonly Color GoldColor   = new(1f, 0.85f, 0.4f);
    private static readonly Color WarnColor   = new(1f, 0.75f, 0.3f);

    // The fetched states themselves are saved by RelayLocalStore (relay_state.json), so they survive a restart.
    // Only transient things live here, so a rebuilt panel still knows a check is running and how the last one went.
    private static bool CheckInFlight;
    private static string? LastCheckNote; // e.g. "2 runs couldn't be checked"

    private Label          _totals     = null!;
    private Label          _liveStatus = null!;
    private Button         _checkButton = null!;
    private VBoxContainer  _rows       = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);

        var top = new HBoxContainer();
        AddChild(top);

        _totals = new Label
        {
            AutowrapMode        = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize   = new Vector2(250, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _totals.AddThemeFontSizeOverride("font_size", 13);
        top.AddChild(_totals);

        _checkButton = new Button
        {
            Text        = "Refresh",
            TooltipText = $"Check these runs on GitHub: how far along they are and whether it's anyone's turn. " +
                          $"Checks up to {MaxLiveRuns} runs that haven't ended.",
        };
        _checkButton.Pressed += () => _ = CheckOnGitHubAsync();
        top.AddChild(_checkButton);

        _liveStatus = new Label
        {
            AutowrapMode      = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(360, 0),
            Modulate          = MutedColor,
        };
        _liveStatus.AddThemeFontSizeOverride("font_size", 12);
        AddChild(_liveStatus);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize    = new Vector2(360, 280),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        AddChild(scroll);

        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_rows);

        Refresh();
    }

    public void Refresh()
    {
        foreach (var child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        var runs = RelayLocalStore.MyRuns();
        UpdateStatusLine(runs);

        if (runs.Count == 0)
        {
            _totals.Text = "No relay turns recorded yet. Turns you pass will show up here.";
            return;
        }

        int turns  = runs.Sum(r => r.Turns);
        int floors = runs.Sum(r => r.FloorsPlayed);
        int won    = runs.Count(r => r.Outcome == "won");
        int lost   = runs.Count(r => r.Outcome == "lost");
        _totals.Text = $"{runs.Count} run{(runs.Count == 1 ? "" : "s")} · {turns} turn{(turns == 1 ? "" : "s")} · " +
                       $"{floors} floors played · {won} won · {lost} lost";

        var live = RelayLocalStore.LiveStates();
        foreach (var run in runs)
            _rows.AddChild(BuildRow(run, live));
    }

    // Shows when the live states were fetched, and gates the button. Never touches the network.
    private void UpdateStatusLine(IReadOnlyList<MyRunSummary> runs)
    {
        bool anyToCheck = runs.Any(r => !r.IsEnded);
        _checkButton.Disabled = CheckInFlight || !anyToCheck;

        if (CheckInFlight)
            _liveStatus.Text = "Checking GitHub...";
        else if (RelayLocalStore.LastChecked() == 0)
            _liveStatus.Text = anyToCheck ? "Press Refresh to check these runs on GitHub." : "";
        else
            _liveStatus.Text = $"Checked {FormatTime(RelayLocalStore.LastChecked())}." +
                               (LastCheckNote is null ? "" : " " + LastCheckNote);
    }

    private async Task CheckOnGitHubAsync()
    {
        if (CheckInFlight) return;

        var controller = RelayTheSpireMod.Controller;
        if (controller is null)
        {
            _liveStatus.Text = "RelayTheSpire.cfg not configured. Check your mod folder.";
            return;
        }

        var toCheck = RelayLocalStore.MyRuns()
            .Where(r => !r.IsEnded) // a run that ended on your turn is final; nothing to look up
            .Take(MaxLiveRuns)
            .Select(r => (r.RoomId, r.RunId))
            .ToList();
        if (toCheck.Count == 0) return;

        CheckInFlight         = true;
        _checkButton.Disabled = true;
        _liveStatus.Text      = "Checking GitHub...";

        try
        {
            var result = await controller.FetchRunStatesAsync(toCheck, RelayTheSpireMod.GetLocalPlayerHash());

            // Saved to disk, so the results are still there after the game is closed. Nothing is saved (and
            // "Checked" doesn't move) if no run could be checked at all.
            if (result.States.Count > 0)
                RelayLocalStore.SaveLive(result.States.Values);

            LastCheckNote = result.Failed == 0
                ? null
                : $"{result.Failed} run{(result.Failed == 1 ? "" : "s")} couldn't be checked" +
                  (result.FirstError is { } e ? $": {Shorten(e)}" : ".");
        }
        catch (Exception ex)
        {
            LastCheckNote = $"Couldn't check GitHub: {Shorten(ex.Message)}";
            Log.Warn($"[RelayTheSpire] My Runs refresh failed: {ex}");
        }
        finally
        {
            CheckInFlight = false;
        }

        // The panel may have been rebuilt while we waited; the results are saved either way.
        if (GodotObject.IsInstanceValid(this) && IsInsideTree())
            Refresh();
    }

    private static string Shorten(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "..." : line;
    }

    // Time only when it was today; otherwise the date too, since saved results can be days old.
    private static string FormatTime(long unixSeconds)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("MMM d, t", CultureInfo.CurrentCulture);
    }

    private static Color ColorFor(LiveKind kind) => kind switch
    {
        LiveKind.Won            => WonColor,
        LiveKind.CanTake        => WonColor,
        LiveKind.Lost           => LostColor,
        LiveKind.YouHoldIt      => GoldColor,
        LiveKind.Missing        => WarnColor,
        LiveKind.Abandoned      => WarnColor,
        _                       => MutedColor,
    };

    private static Control BuildRow(MyRunSummary run, IReadOnlyDictionary<(string RoomId, string RunId), RunLiveState> liveStates)
    {
        var row = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Stop, // so the tooltip shows
            TooltipText = $"Run {run.RunId}\nFirst played {FormatDate(run.FirstPlayed)}",
        };
        row.AddThemeConstantOverride("separation", 2);

        // Title line: "Necrobinder - Friday crew", with a button to copy the run ID.
        var top = new HBoxContainer();
        row.AddChild(top);

        var title = new Label
        {
            Text                = $"{FriendlyCharacter(run.CharacterId)} · {RelayLocalStore.RoomName(run.RoomId)}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText            = true,
        };
        title.AddThemeFontSizeOverride("font_size", 15);
        top.AddChild(title);

        var copy = new Button { Text = "Copy run seed"};
        copy.Pressed += () => DisplayServer.ClipboardSet(run.RunId);
        top.AddChild(copy);

        // Status line: how the run stood after your last turn.
        var status = new Label();
        status.AddThemeFontSizeOverride("font_size", 13);
        switch (run.Outcome)
        {
            case "won":
                status.Text = $"Won at floor {run.LastFloor}";
                status.Modulate = WonColor;
                break;
            case "lost":
                status.Text = $"Lost at floor {run.LastFloor}";
                status.Modulate = LostColor;
                break;
            case not null:
                status.Text = $"Ended at floor {run.LastFloor}";
                status.Modulate = MutedColor;
                break;
            default:
                status.Text = $"After your last turn: floor {run.LastFloor}";
                status.Modulate = MutedColor;
                break;
        }
        row.AddChild(status);

        // Live line: only for runs you haven't seen end, and only after a Refresh.
        if (!run.IsEnded && liveStates.TryGetValue((run.RoomId, run.RunId), out var live))
        {
            var now = new Label
            {
                Text              = $"Now: {live.Text}",
                Modulate          = ColorFor(live.Kind),
                AutowrapMode      = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(340, 0),
            };
            now.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(now);
        }

        var detail = new Label
        {
            Text     = $"Your turns: {run.Turns} · floors played: {run.FloorsPlayed} · last played {FormatDate(run.LastPlayed)}",
            Modulate = MutedColor,
        };
        detail.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(detail);

        row.AddChild(new HSeparator());
        return row;
    }

    /// <summary>"CHARACTER.NECROBINDER" becomes "Necrobinder"; anything unexpected is shown tidied up, never blank.</summary>
    private static string FriendlyCharacter(string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId)) return "Unknown character";

        var name = characterId.Contains('.') ? characterId[(characterId.LastIndexOf('.') + 1)..] : characterId;
        name = name.Replace('_', ' ').ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
    }

    private static string FormatDate(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
}
