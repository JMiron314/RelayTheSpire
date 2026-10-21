using Godot;
using MegaCrit.Sts2.Core.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
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
    private Button    _createButton = null!;
    private RelayMyRunsView _myRuns = null!;
    private Label     _statusLabel = null!;
    private OptionButton _picker = null!;
    private Button    _forgetButton = null!;
    private TabContainer _tabs = null!;
    private RelayRoomsView _rooms = null!;
    private Button _revealButton = null!;
    
    // _picker items: 0 = placeholder, 1 = "Community" (clears the field), then one item per recent room.
    private const int PickerPlaceholder = 0;
    private const int PickerCommunity   = 1;
    private const int PickerFirstRoom   = 2;
    private readonly List<string> _pickerRooms = new();

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
    
    private static void StyleButton(Button button, Color normal, Color hover, Color pressed)
    {
        StyleBoxFlat Make(Color color)
        {
            var box = new StyleBoxFlat { BgColor = color };
            box.SetCornerRadiusAll(4);
            box.SetContentMarginAll(8);
            return box;
        }

        button.AddThemeStyleboxOverride("normal",   Make(normal));
        button.AddThemeStyleboxOverride("hover",    Make(hover));
        button.AddThemeStyleboxOverride("pressed",  Make(pressed));
        button.AddThemeStyleboxOverride("disabled", Make(normal.Darkened(0.4f)));

        // Optional: text color
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(460, 0);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_top",    16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        margin.AddThemeConstantOverride("margin_left",   20);
        margin.AddThemeConstantOverride("margin_right",  20);
        AddChild(margin);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 12);
        margin.AddChild(outer);

        // Title
        var title = new Label { Text = "RelayTheSpire" };
        title.AddThemeFontSizeOverride("font_size", 20);
        outer.AddChild(title);

        outer.AddChild(new HSeparator());

        // Tabs: "Play" holds everything below that is added to vbox; "My Runs" is the history view.
        // Tabs: "Play" holds everything below that is added to vbox; "My Runs" is the history view;
        // "Rooms" is the public room browser.
        // Fixed size: the panel must not resize when the tab or the status text changes.
        // Play is wrapped in a scroll container so its content can't push the panel bigger;
        // My Runs and Rooms already scroll their own lists.
        _tabs = new TabContainer { CustomMinimumSize = new Vector2(440, 420) };
        outer.AddChild(_tabs);

        var playScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            SizeFlagsVertical    = SizeFlags.ExpandFill,
        };
        _tabs.AddChild(playScroll);

        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 12);
        playScroll.AddChild(vbox);

        _myRuns = new RelayMyRunsView();
        _tabs.AddChild(_myRuns);

        _rooms = new RelayRoomsView();
        _rooms.JoinRequested += OnJoinRoom;
        _tabs.AddChild(_rooms);

        _tabs.SetTabTitle(0, "Play");
        _tabs.SetTabTitle(1, "My Runs");
        _tabs.SetTabTitle(2, "Rooms");
        _tabs.TabChanged += index =>
        {
            if (index == 1) _myRuns.Refresh();
            else if (index == 2) _rooms.OnOpened();
        };
        
        // Take the Baton: one click, picks the best run for this player.
        _batonButton = new Button
        {
            Text        = "Take the Baton!",
            TooltipText = "Takes the next run waiting for you. Leave the Room ID blank to play in " +
                          "the Community room, or enter a room ID to play with friends.",
        };
        _batonButton.Pressed += OnBatonPressed;
        vbox.AddChild(new HSeparator());
        vbox.AddChild(_batonButton);
        StyleButton(_batonButton,
            normal:  new Color(0.15f, 0.45f, 0.2f),
            hover:   new Color(0.2f, 0.55f, 0.27f),
            pressed: new Color(0.1f, 0.35f, 0.15f));
        vbox.AddChild(new HSeparator());
        
        var orLabel = new Label { Text = "Room ID (leave blank for the Community room):" };
        orLabel.AddThemeFontSizeOverride("font_size", 13);
        orLabel.Modulate = new Color(0.8f, 0.8f, 0.8f);
        vbox.AddChild(orLabel);

        // Run ID row
        var runIdRow = new HBoxContainer();
        vbox.AddChild(runIdRow);

        var runIdLabel = new Label { Text = "Room ID:" };
        runIdLabel.CustomMinimumSize = new Vector2(70, 0);
        runIdRow.AddChild(runIdLabel);

        _runIdInput = new LineEdit
        {
            PlaceholderText     = "Community",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _runIdInput.TextSubmitted += _ => OnBatonPressed();
        // LineEdit only raises TextChanged for typing/pasting, not for code. Code that sets the text goes
        // through SetRoomText, which updates RelayStartContext itself.
        _runIdInput.TextChanged   += text => { RelayStartContext.LastRoomId = text; SyncPicker(); };
        _runIdInput.FocusExited   += RelayLocalStore.Flush;
        _runIdInput.Text           = RelayStartContext.LastRoomId; // remembered across restarts until cleared
        runIdRow.AddChild(_runIdInput);
        
        _revealButton = new Button
        {
            Text        = "Hide",
            TooltipText = "Hide or show the room code (useful when streaming).",
        };
        _revealButton.Pressed += () => SetCodeHidden(!RelayStartContext.CodeHidden);
        runIdRow.AddChild(_revealButton);

        // Recent rooms: pick one to fill the field, or "Forget" to drop the one in the field.
        var pickerRow = new HBoxContainer();
        vbox.AddChild(pickerRow);

        _picker = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText            = true,
            TooltipText         = "Rooms you've used recently. Pick one to fill the Room ID field.",
        };
        _picker.ItemSelected += OnPickerSelected;
        pickerRow.AddChild(_picker);

        _forgetButton = new Button
        {
            Text        = "Forget",
            TooltipText = "Remove the room in the Room ID field from your recent rooms.",
        };
        _forgetButton.Pressed += OnForgetPressed;
        pickerRow.AddChild(_forgetButton);

        RebuildPicker();

        _createButton = new Button
        {
            Text        = "Create New Room",
        };
        _createButton.Pressed += OnCreatePressed;
        vbox.AddChild(_createButton);

        vbox.AddChild(new HSeparator());

        // Status label
        _statusLabel = new Label
        {
            Text         = "Click Take the Baton to join a run.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        vbox.AddChild(_statusLabel);
        
        ApplyCodeHidden();

        // The panel is a child of the main menu, so without this it would draw on top of every submenu
        // (character select, settings...). Follow the menu's own button group: hidden while a submenu is open.
        if (FindMainMenu() is { SubmenuStack: { } stack })
        {
            Visible = !stack.SubmenusOpen;
            _ = stack.Connect(NSubmenuStack.SignalName.StackModified,
                              Callable.From(() => Visible = !stack.SubmenusOpen));
        }

        // Shown once after a forced exit to the menu following a successful push.
        if (RelayTheSpireMod.PendingMenuMessage is { } pending)
        {
            SetStatus(pending, isError: false);
            RelayTheSpireMod.PendingMenuMessage = null;
        }
        
        // Once per launch: warns if other gameplay-altering mods are loaded.
        RelayModCheck.ShowIfNeeded(this);
    }

    // -------------------------------------------------------------------------
    // Pull logic
    // -------------------------------------------------------------------------

    // Returns null on success, otherwise a human-readable reason for the failure.
    public override void _ExitTree() => RelayLocalStore.Flush();

    // -------------------------------------------------------------------------
    // Recent rooms picker
    // -------------------------------------------------------------------------

    private void RebuildPicker()
    {
        _picker.Clear();
        _pickerRooms.Clear();

        _picker.AddItem("Recent rooms...");
        _picker.AddItem("Community");
        foreach (var room in RelayLocalStore.RecentRooms())
        {
            _picker.AddItem(RelayStartContext.CodeHidden && string.IsNullOrEmpty(room.Name) ? "Unnamed room" : room.Label);
            _pickerRooms.Add(room.RoomId);
        }

        SyncPicker();
    }

    // Makes the picker show what the field says: a recent room, Community (blank), or the placeholder.
    private void SyncPicker()
    {
        int index = PickerPlaceholder;
        if (string.IsNullOrWhiteSpace(_runIdInput.Text))
            index = PickerCommunity;
        else if (RelayScope.TryNormalizeRoomId(_runIdInput.Text, out var id) && _pickerRooms.IndexOf(id) is var i and >= 0)
            index = PickerFirstRoom + i;

        _picker.Select(index);
        _forgetButton.Disabled = !RelayLocalStore.IsRecent(_runIdInput.Text);
    }

    private void SetRoomText(string text)
    {
        _runIdInput.Text = text;
        RelayStartContext.LastRoomId = text;
        RelayLocalStore.Flush();
        SyncPicker();
    }

    private void OnPickerSelected(long index)
    {
        if (index == PickerCommunity)
            SetRoomText("");
        else if (index >= PickerFirstRoom && index - PickerFirstRoom < _pickerRooms.Count)
            SetRoomText(_pickerRooms[(int)(index - PickerFirstRoom)]);
        else
            SyncPicker(); // the placeholder isn't a choice
    }

    private void OnForgetPressed()
    {
        RelayLocalStore.Forget(_runIdInput.Text);
        RebuildPicker();
        SetStatus("Removed that room from your recent rooms.", isError: false);
    }
    
    // Join from the Rooms tab: fill in the Room ID field and go back to Play. Doesn't take the baton.
    private void OnJoinRoom(RoomSettings room)
    {
        bool community = room.RoomId == RelayScope.CommunityRoomId;

        if (!community)
        {
            RelayLocalStore.Touch(room.RoomId, room.Name); // so it shows up in Recent rooms
            RebuildPicker();
        }

        SetRoomText(community ? "" : room.RoomId);
        _tabs.CurrentTab = 0;
        SetStatus($"Room set to {RelayStartContext.NameOf(room)}. Click Take the Baton to play.", isError: false);
    }

    private NMainMenu? FindMainMenu()
    {
        Node? node = GetParent();
        while (node != null && node is not NMainMenu)
            node = node.GetParent();
        return node as NMainMenu;
    }

    private string? RefreshMainMenu()
    {
        var mainMenu = FindMainMenu();
        if (mainMenu is null)
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

    private void OnBatonPressed()
    {
        if (RelayTheSpireMod.Controller is null)
        {
            SetStatus("RelayTheSpire.cfg not configured. Check your mod folder.", isError: true);
            return;
        }

        RelayLocalStore.Flush();
        _ = DoTakeBaton();
    }

    private async Task DoTakeBaton()
    {
        SetStatus("Looking for a run that needs a player...", isError: false);
        SetBusy(true);

        try
        {
            var playerHash = RelayTheSpireMod.GetLocalPlayerHash();
            // Blank field = the Community room only. With a room ID, only that room.
            var search = await RelayTheSpireMod.Controller!.FindBatonAsync(playerHash, _runIdInput.Text);

            if (!search.Success)
            {
                SetStatus($"Error: {search.Error}", isError: true);
                Log.Warn($"[RelayTheSpire] Baton search failed: {search.Error}");
                return;
            }

            var room  = search.Room!;
            var stats = search.Stats;

            if (search.Outcome != BatonOutcome.TakeRun)
            {
                Log.Info($"[RelayTheSpire] Baton search in {RelayStartContext.NameOf(room)}: nothing to take ({search.Outcome}; " +
                         $"{stats.Total} total, {stats.Ongoing} ongoing, {stats.Ended} ended, {stats.Available} available).");

                if (search.Outcome == BatonOutcome.StartRun)
                {
                    // Below the room's run target: start a new run without asking.
                    BeginStartFlow(room);
                    return;
                }

                // At or above the target: say what's going on and let the player decide.
                var message = $"No run is waiting for you in {RelayStartContext.NameOf(room)} " +
                              $"({stats.Ongoing} ongoing, {stats.Ended} finished). Start a new run?";
                SetStatus(message, isError: false);
                ConfirmStart(message, () => BeginStartFlow(room));
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

            SetStatus($"Found run {search.RunId} in {RelayStartContext.NameOf(room)} at floor {manifest.FloorsCompleted}. Downloading...",
                      isError: false);
            Log.Info($"[RelayTheSpire] Baton search picked run {search.RunId} in {room.RoomId} " +
                     $"(floor {manifest.FloorsCompleted}; {stats.Available} of {stats.Total} runs available).");

            await DoPull(room, search.RunId!);
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

    private void OnCreatePressed()
    {
        if (RelayTheSpireMod.Controller is null)
        {
            SetStatus("RelayTheSpire.cfg not configured. Check your mod folder.", isError: true);
            return;
        }

        var dialog = new RelayCreateRoomDialog();
        dialog.RoomChosen += desired => { _ = DoCreateRoom(desired); };
        AddChild(dialog);
        dialog.PopupCentered();
    }

    private async Task DoCreateRoom(RoomSettings desired)
    {
        SetStatus("Creating a private room...", isError: false);
        SetBusy(true);

        try
        {
            var result = await RelayTheSpireMod.Controller!.CreateRoomAsync(desired);
            if (!result.Success)
            {
                SetStatus($"Error: {result.Error}", isError: true);
                Log.Warn($"[RelayTheSpire] Create room failed: {result.Error}");
                return;
            }

            var room = result.Room!;
            DisplayServer.ClipboardSet(room.RoomId);
            RelayLocalStore.Touch(room.RoomId, room.Name);
            RebuildPicker();
            SetRoomText(room.RoomId);

            var listedNote = !room.Listed ? ""
                : result.Warning is { } warning ? $"\nNote: {warning}"
                : "\nListed publicly: it will show up in the Rooms tab.";

            var named  = room.Name is null ? "Room" : $"\"{room.DisplayName}\"";
            var idText = RelayStartContext.CodeHidden
                ? "The Room ID is hidden but copied to clipboard."
                : $"Room ID: {room.RoomId} (copied to clipboard).";

            SetStatus(
                $"{named} created! {idText} Share this with your friends!\n" +
                $"{room.RunCount} runs, {room.FloorsPerTurn} floors per turn. {room.Policy.Describe()}" +
                listedNote + "\n" +
                "Click Take the Baton to start the first run.",
                isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Unexpected error: {ex.Message}", isError: true);
            Log.Error($"[RelayTheSpire] Create room exception: {ex}");
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
    
    private void SetCodeHidden(bool hidden)
    {
        RelayStartContext.CodeHidden = hidden;
        ApplyCodeHidden();
    }

    private void ApplyCodeHidden()
    {
        bool hidden        = RelayStartContext.CodeHidden;
        _runIdInput.Secret = hidden;
        _revealButton.Text = hidden ? "Show" : "Hide";
        RebuildPicker();
    }

    /// <summary>
    /// Opens character select for a new relay run in the given room. Takes the same three steps the game's
    /// own Singleplayer button takes; the "Relay run" checkbox arrives already ticked and pointed at the room
    /// (RelayStartContext), and RelayEmbarkPatch registers the run when the player embarks.
    /// </summary>
    private void BeginStartFlow(RoomSettings room)
    {
        if (SaveManager.Instance.HasRunSave)
        {
            var msg = "You already have a run in progress. Continue or abandon it before starting a new relay run.";
            SetStatus(msg, isError: true);
            ShowPopup(msg);
            return;
        }

        var mainMenu = FindMainMenu();
        if (mainMenu is null)
        {
            SetStatus("Couldn't find the main menu to open character select.", isError: true);
            return;
        }

        try
        {
            RelayStartContext.Arm(room);

            var screen = mainMenu.SubmenuStack.GetSubmenuType<NCharacterSelectScreen>();
            screen.InitializeSingleplayer();
            mainMenu.SubmenuStack.Push((NSubmenu) screen);

            SetStatus($"Pick a character to start a new run in {RelayStartContext.NameOf(room)}.", isError: false);
            Log.Info($"[RelayTheSpire] Opened character select for a new run in {room.RoomId}.");
        }
        catch (Exception ex)
        {
            RelayStartContext.Clear();
            SetStatus($"Couldn't open character select: {ex.Message}", isError: true);
            Log.Error($"[RelayTheSpire] Opening character select failed: {ex}");
        }
    }

    private void ConfirmStart(string message, Action onConfirm)
    {
        var dialog = new ConfirmationDialog
        {
            Title         = "RelayTheSpire",
            DialogText    = message,
            OkButtonText  = "Start a new run",
            CancelButtonText = "Cancel",
        };
        dialog.Confirmed += () => { dialog.QueueFree(); onConfirm(); };
        dialog.Canceled  += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
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
        _createButton.Disabled = busy;
        _picker.Disabled      = busy;
        _forgetButton.Disabled = busy || !RelayLocalStore.IsRecent(_runIdInput.Text);
    }

    private async Task DoPull(RoomSettings room, string runId)
    {
        if (SaveManager.Instance.HasRunSave)
        {
            SetStatus("You already have a run in progress. Continue or abandon it before taking the baton.", isError: true);
            return;
        }
        SetStatus("Downloading run save...", isError: false);
        SetBusy(true);

        try
        {
            var playerHash = RelayTheSpireMod.GetLocalPlayerHash();
            
            LogSaveDiagnostics("before pull");

            var result = await RelayTheSpireMod.Controller!.PullRelayAsync(room, runId, playerHash);

            LogSaveDiagnostics("after pull");

            if (result.Success)
            {
                RelayLocalStore.Touch(room.RoomId, room.Name);
                RebuildPicker();

                var floor = result.Save?.VisitedMapCoordsCount ?? -1;
                var seed  = result.Save?.Seed ?? runId;

                var refreshError = RefreshMainMenu();
                
                if (refreshError == null)
                {
                    SetStatus(
                        $"Run received! Floor {floor}, seed {seed}.\n" +
                        "Click 'Continue' in the main menu to start your turn.",
                        isError: false);
                }
                else
                {
                    SetStatus(
                        $"Run saved (floor {floor}, seed {seed}), but the menu didn't refresh: {refreshError}",
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