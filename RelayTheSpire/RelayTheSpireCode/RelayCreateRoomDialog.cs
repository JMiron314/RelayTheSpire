using Godot;
using System;
using System.Collections.Generic;

namespace RelayTheSpire.UI;

/// <summary>
/// The "Create a private room" dialog. Collects a name and the room's rules, shows what they mean in plain
/// words, and hands the finished <see cref="RoomSettings"/> to <see cref="RoomChosen"/> when the player confirms.
/// It does no network work itself; RelayMenuPanel creates the room.
///
/// "Players in your group" is only a helper: changing it fills in the runs and repeat rule that
/// <see cref="RoomSettings.Recommend"/> suggests (runs = players + 1, wait for everyone else). The player can
/// then change any number. Rooms are always private (unlisted) here. The pick order is chosen separately and is not affected by the helper.
/// </summary>
public partial class RelayCreateRoomDialog : ConfirmationDialog
{
    private const int MinPlayers   = 2;
    private const int MaxPlayers   = 20;
    private const int MaxCooldown  = 50;
    private const int RepeatWait   = 0;
    private const int RepeatOnce   = 1;
    private const int PickLowest  = 0;
    private const int PickHighest = 1;
    private const int PickRandom  = 2;

    private OptionButton _pickOrder = null!;
    private const string PreviewId = "PREVIEW0"; // stands in for the real ID, which is generated on confirm

    /// <summary>Raised once when the player confirms, with settings that already pass validation.</summary>
    public event Action<RoomSettings>? RoomChosen;

    private LineEdit     _name         = null!;
    private SpinBox      _players      = null!;
    private SpinBox      _runs         = null!;
    private SpinBox      _floors       = null!;
    private SpinBox      _cooldown     = null!;
    private OptionButton _repeatMode   = null!;
    private Label        _cooldownText = null!;
    private Label        _summary      = null!;
    private Label        _warnings     = null!;
    private CheckBox     _listed       = null!;

    public override void _Ready()
    {
        Title            = "Create a private room";
        OkButtonText     = "Create room";
        CancelButtonText = "Cancel";
        MinSize          = new Vector2I(460, 0);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        AddChild(root);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 8);
        root.AddChild(grid);

        _name = new LineEdit
        {
            PlaceholderText     = "e.g. Spire Slayers (optional)",
            MaxLength           = RoomSettings.MaxNameLength,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize   = new Vector2(220, 0),
        };
        AddRow(grid, "Room name", _name);

        _players = NewSpin(MinPlayers, MaxPlayers, 3);
        _players.TooltipText = "Suggests the number of runs and the repeat rule below.";
        AddRow(grid, "Players in your group (optional)", _players);

        _runs = NewSpin(1, RoomSettings.MaxRunCount, RoomSettings.DefaultRunCount);
        _runs.TooltipText = "How many runs are created at the start (can be increased later).";
        AddRow(grid, "Starting number of runs", _runs);

        _floors = NewSpin(1, RoomSettings.MaxFloorsPerTurn, RoomSettings.DefaultFloorsPerTurn);
        _floors.TooltipText = "Floors each player plays before passing the baton.";
        AddRow(grid, "Floors played before passing the baton", _floors);

        _repeatMode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _repeatMode.AddItem("Wait for other turns");
        _repeatMode.AddItem("Once per run");
        _repeatMode.TooltipText = "Whether a player may take another turn in a run they already played.";
        AddRow(grid, "Taking turns again", _repeatMode);

        _cooldown = NewSpin(1, MaxCooldown, RoomSettings.DefaultPolicy.CooldownTurns ?? 1);
        _cooldownText = AddRow(grid, "Other turns first", _cooldown);
        
        _pickOrder = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _pickOrder.AddItem("Lowest floor first");
        _pickOrder.AddItem("Highest floor first");
        _pickOrder.AddItem("Random");
        _pickOrder.TooltipText =
            "Which waiting run is taken first. Lowest keeps runs balanced, highest pushes one run " +
            "as deep as possible, random picks any run you can take.";
        AddRow(grid, "Run pick order", _pickOrder);
        
        _listed = new CheckBox
        {
            Text        = "List this room publicly",
            TooltipText = "Shows the room in everyone's Rooms tab. This can't be undone: a room's settings never " +
                          "change after it is created. Unlisted rooms are joined by ID only.",
        };
        AddRow(grid, "Visibility", _listed);

        _summary = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _summary.CustomMinimumSize = new Vector2(400, 0);
        root.AddChild(_summary);

        _warnings = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1f, 0.75f, 0.3f) };
        _warnings.CustomMinimumSize = new Vector2(400, 0);
        root.AddChild(_warnings);

        _players.ValueChanged  += _ => ApplyRecommendation();
        _runs.ValueChanged     += _ => Refresh();
        _floors.ValueChanged   += _ => Refresh();
        _cooldown.ValueChanged += _ => Refresh();
        _repeatMode.ItemSelected += _ => Refresh();
        _pickOrder.ItemSelected += _ => Refresh();
        _listed.Toggled += _ => Refresh();

        Confirmed += OnConfirmed;
        Canceled  += QueueFree;

        Refresh();
    }

    // -------------------------------------------------------------------------

    private static SpinBox NewSpin(int min, int max, int value) => new()
    {
        MinValue            = min,
        MaxValue            = max,
        Step                = 1,
        Rounded             = true,
        Value               = value,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };

    // Adds "label | control" and returns the label so a row can be hidden together with its control.
    private static Label AddRow(GridContainer grid, string text, Control control)
    {
        var label = new Label { Text = text };
        grid.AddChild(label);
        grid.AddChild(control);
        return label;
    }

    private void ApplyRecommendation()
    {
        var (runs, policy) = RoomSettings.Recommend((int)_players.Value);

        _runs.Value = runs;
        _repeatMode.Select(RepeatWait);
        _cooldown.Value = policy.CooldownTurns ?? 1;

        Refresh();
    }

    private RoomSettings BuildSettings(string roomId)
    {
        var policy = _repeatMode.Selected == RepeatOnce
            ? RepeatPolicy.OncePerRun
            : RepeatPolicy.Cooldown((int)_cooldown.Value);

        var settings = RoomSettings.Create(roomId, (int)_runs.Value, (int)_floors.Value, policy, _name.Text);
        settings.Listed = _listed.ButtonPressed;
        settings.Order  = _pickOrder.Selected switch
        {
            PickHighest => PickOrder.HighestFloor,
            PickRandom  => PickOrder.Random,
            _           => PickOrder.LowestFloor,
        };
        return settings;
    }

    // Re-reads the controls: shows/hides the cooldown row, rewrites the summary, shows warnings, gates OK.
    private void Refresh()
    {
        bool waiting = _repeatMode.Selected == RepeatWait;
        _cooldown.Visible     = waiting;
        _cooldownText.Visible = waiting;

        var settings = BuildSettings(PreviewId);
        var problem  = settings.Validate();

        _summary.Text = problem ??
            $"{settings.RunCount} run{(settings.RunCount == 1 ? "" : "s")}, " +
            $"{settings.FloorsPerTurn} floor{(settings.FloorsPerTurn == 1 ? "" : "s")} per turn, " +
            $"{settings.Order.Describe()}. " +
            settings.Policy.Describe() +
            (settings.Listed ? "\nListed publicly: anyone can find and join it, and this can't be changed later." : "");

        var warnings = new List<string>(settings.Warnings());

        int players = (int)_players.Value;
        if (settings.Policy.IsOncePerRun && players * settings.FloorsPerTurn < 51)
            warnings.Add($"With {players} players and {settings.FloorsPerTurn} floors per turn, " +
                         "you may not be able to finish a run if players cannot repeat.");

        _warnings.Text    = string.Join("\n", warnings);
        _warnings.Visible = warnings.Count > 0;

        GetOkButton().Disabled = problem is not null;
    }

    private void OnConfirmed()
    {
        var settings = BuildSettings(RelayScope.GenerateRoomId());
        if (settings.Validate() is null)
            RoomChosen?.Invoke(settings);

        QueueFree();
    }
}
