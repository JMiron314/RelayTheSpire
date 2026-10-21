using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace RelayTheSpire.UI;

/// <summary>
/// "Relay run" checkbox injected into the character-select screen (see RelaySceneInjections).
/// Ticking it makes the next embark register the run in a relay room first (see RelayEmbarkPatch).
/// Only shown for singleplayer lobbies. The screen's open/close patches call Refresh.
/// </summary>
public partial class RelayRunToggle : PanelContainer
{
    public const string NodeName = "RelayRunToggle";

    private CheckBox _check = null!;
    private Label    _target = null!;

    public override void _Ready()
    {
        Name = NodeName;
        CustomMinimumSize = new Vector2(240, 0);

        // Top-right corner. Eyeball this in game: the scene layout isn't known to the mod.
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        GrowHorizontal = GrowDirection.Begin;
        OffsetRight    = -24f;
        OffsetTop      = 24f;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_top",    8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        margin.AddThemeConstantOverride("margin_left",   12);
        margin.AddThemeConstantOverride("margin_right",  12);
        AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 2);
        margin.AddChild(vbox);

        _check = new CheckBox
        {
            Text        = "Relay run",
            TooltipText = "Register this run in a relay room, then play your floors and pass the baton.",
        };
        _check.Toggled += on =>
        {
            RelayStartContext.Enabled = on;
            UpdateTarget();
        };
        vbox.AddChild(_check);

        _target = new Label();
        _target.AddThemeFontSizeOverride("font_size", 13);
        _target.Modulate = new Color(0.8f, 0.8f, 0.8f);
        vbox.AddChild(_target);

        Visible = false;
    }

    /// <summary>Syncs the checkbox with RelayStartContext and shows/hides it for this lobby type.</summary>
    public void Refresh(NCharacterSelectScreen screen)
    {
        bool singleplayer = screen.Lobby?.NetService.Type == NetGameType.Singleplayer;
        if (!singleplayer)
            RelayStartContext.Enabled = false;

        Visible = singleplayer;
        _check.SetPressedNoSignal(RelayStartContext.Enabled);
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        _target.Text    = RelayStartContext.Enabled ? $"-> {RelayStartContext.DescribeTarget()}" : "";
        _target.Visible = RelayStartContext.Enabled;
    }
}
