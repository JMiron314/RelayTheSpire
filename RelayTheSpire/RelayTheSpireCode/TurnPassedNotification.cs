using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace RelayTheSpire.UI;

/// <summary>
/// Toast-style notification shown in the top-center of the run screen after
/// the relay push completes. Fades in, holds for a few seconds, then fades out.
///
/// Wired up in RelaySceneInjections — subscribes to RelayTheSpireMod.TurnPassed
/// and RelayTheSpireMod.PushFailed automatically when injected into NRun.
/// </summary>
public partial class TurnPassedNotification : PanelContainer
{
    private Label _messageLabel = null!;
    private Label _detailLabel  = null!;

    private const float DisplaySeconds = 5f;
    private const float FadeSeconds    = 0.8f;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        GrowVertical  = GrowDirection.End;
        MouseFilter   = MouseFilterEnum.Ignore;
        Modulate      = Colors.Transparent; // Hidden until Show() is called.

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_top",    10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        margin.AddThemeConstantOverride("margin_left",   24);
        margin.AddThemeConstantOverride("margin_right",  24);
        AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        margin.AddChild(vbox);

        _messageLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _messageLabel.AddThemeFontSizeOverride("font_size", 16);
        vbox.AddChild(_messageLabel);

        _detailLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _detailLabel.AddThemeFontSizeOverride("font_size", 13);
        _detailLabel.Modulate = new Color(0.8f, 0.8f, 0.8f);
        vbox.AddChild(_detailLabel);
    }

    // -------------------------------------------------------------------------
    // Public API — called via RelayTheSpireMod events
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shows a success notification. Signature matches Action&lt;ActiveSaveData, RelayManifest&gt;.
    /// </summary>
    public void Show(ActiveSaveData save, RelayManifest manifest)
    {
        _messageLabel.Modulate = Colors.White;
        _messageLabel.Text     = $"Turn passed! Floor {save.VisitedMapCoordsCount} pushed to relay.";
        _detailLabel.Text      = $"Run ID: {save.Seed}  •  Turn {manifest.RelayChain.Count}";
        FadeInThenOut(DisplaySeconds);
    }

    /// <summary>
    /// Shows an error notification. Signature matches Action&lt;string&gt;.
    /// </summary>
    public void ShowError(string error)
    {
        _messageLabel.Modulate = new Color(1f, 0.4f, 0.4f);
        _messageLabel.Text     = "Relay push failed!";
        _detailLabel.Text      = error.Length > 80 ? error[..80] + "..." : error;
        FadeInThenOut(DisplaySeconds + 2f);
    }

    // -------------------------------------------------------------------------
    // Animation
    // -------------------------------------------------------------------------

    private void FadeInThenOut(float holdSeconds)
    {
        // Kill any existing tween so rapid calls don't stack weirdly.
        var tween = CreateTween();
        tween.TweenProperty(this, "modulate:a", 1f, FadeSeconds * 0.5f);
        tween.TweenInterval(holdSeconds);
        tween.TweenProperty(this, "modulate:a", 0f, FadeSeconds);
    }
}
