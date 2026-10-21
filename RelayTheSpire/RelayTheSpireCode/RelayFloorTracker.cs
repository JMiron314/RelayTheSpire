using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.sts2.Core.Nodes.TopBar;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RelayTheSpire.UI;

/// <summary>
/// Where the player stands in their current relay turn, worked out from the local manifest and
/// current_run.save (the same two files the push logic in EnterMapCoordPatch uses, so the number
/// shown here always agrees with when the turn will actually be passed).
/// </summary>
internal readonly record struct FloorProgress(int Played, int PerTurn, bool TurnPassed, bool Ended)
{
    public int Left => Math.Max(0, PerTurn - Played);

    /// <summary>Null when the current run isn't a relay run (or the files can't be read).</summary>
    public static FloorProgress? Read()
    {
        try
        {
            var manifestPath = RelaySavePaths.GetFilePath(RelayManifest.FileName);
            var savePath     = RelaySavePaths.GetFilePath("current_run.save");
            if (!File.Exists(manifestPath) || !File.Exists(savePath)) return null;

            var manifest = RelayManifest.FromJson(File.ReadAllText(manifestPath));
            var save     = ActiveSaveData.Parse(File.ReadAllText(savePath));
            if (manifest is null || save is null || !manifest.IsRoomRun || !manifest.MatchesRun(save))
                return null;

            int perTurn = Math.Max(1, manifest.FloorsPerTurn);
            int played  = Math.Clamp(save.VisitedMapCoordsCount - manifest.FloorsCompleted, 0, perTurn);
            bool passed = manifest.HasTakenCurrentTurn(RelayTheSpireMod.GetLocalPlayerHash());

            return new FloorProgress(played, perTurn, passed, manifest.IsEnded);
        }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] Floor progress unavailable: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// A bare "played / per-turn" counter (e.g. "2 / 5") in the top bar, just right of the boss icon.
///
/// It is a plain Label with no background. It is injected into NRun, then moved into the top bar's own node
/// tree as an ordinary (NOT top-level) child, so it inherits the top bar's visibility and modulate and fades
/// with the rest of the GUI during screen transitions. A top-level control would be detached from that.
///
/// Placement: the boss icon (NTopBarBossIcon) gives the position, set in global space every frame. The host is the
/// nearest non-Container ancestor of the icon (a Container would re-arrange the label and make room for it).
/// NTopBarBossIcon hides itself in boss rooms; while it is hidden the label keeps its last position.
///
/// Refreshes when RelayTheSpireMod.FloorProgressChanged fires (after each node is entered). The subscription is
/// tied to the node entering and leaving the tree (idempotent), because Reparent exits and re-enters the tree.
/// </summary>
public partial class RelayFloorTracker : Label
{
    private const float Gap            = 28f;  // pixels right of the boss icon
    private const float VerticalOffset = -3f;   // pixels down from centred on the icon (negative = up)
    private const double HoverDelaySeconds = 0; // how long the mouse must rest on the counter (0 = instant)
    private const double GiveUpSeconds = 5.0;

    private NTopBarBossIcon? _anchor;
    private Vector2?         _lastPosition;
    private double           _waited;
    private bool             _gaveUp;
    private bool             _loggedLayout;
    private string           _lastText = "";

    private PanelContainer   _tip     = null!; // our own hover box (a child, so it fades with the top bar)
    private Label            _tipText = null!;
    private int              _hoverToken;

    public override void _Ready()
    {
        // Pass (not Ignore) so the label receives the mouse for hover, without swallowing clicks.
        MouseFilter = MouseFilterEnum.Pass;
        MouseEntered += () => _ = ShowTipAfterDelayAsync();
        MouseExited  += HideTip;

        BuildTip();

        // No background, so a dark outline keeps the number readable over the top bar art.
        AddThemeFontSizeOverride("font_size", 28);
        AddThemeConstantOverride("outline_size", 6);
        AddThemeColorOverride("font_outline_color", Colors.Black);
        AddThemeColorOverride("font_color", Color.FromHtml("#c5dace"));

        // Until the boss icon is found, sit at the top of the screen.
        SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        OffsetLeft = 360f;
        OffsetTop  = 8f;

        Refresh();
    }

    // Subscribe on entering the tree and unsubscribe on leaving it. Reparent() leaves and re-enters, so the
    // "-=" before "+=" keeps exactly one subscription across any number of reparents.
    public override void _EnterTree()
    {
        RelayTheSpireMod.FloorProgressChanged -= OnChanged;
        RelayTheSpireMod.FloorProgressChanged += OnChanged;
    }

    public override void _ExitTree() => RelayTheSpireMod.FloorProgressChanged -= OnChanged;

    private void OnChanged() => Callable.From(Refresh).CallDeferred();

    public void Refresh()
    {
        if (!GodotObject.IsInstanceValid(this)) return;

        if (FloorProgress.Read() is not { } p)
        {
            Visible = false; // not a relay run
            return;
        }

        Visible = true;

        var text = $"{p.Played} / {p.PerTurn}";
        if (text == _lastText) return;

        _lastText = text;
        Text = text;
        _tipText.Text = p.Left > 0
            ? $"Floors played: {p.Played} of {p.PerTurn}.\n" +
              $"The baton is passed after {p.Left} more floor{(p.Left == 1 ? "" : "s")}."
            : $"Floors played this turn: {p.Played} of {p.PerTurn}.\nThis turn is complete.";
        _tip.ResetSize();
        ResetSize(); // let the label shrink back when the text gets shorter
        Log.Info($"[RelayTheSpire] Floor tracker: {text}");
    }

    // -------------------------------------------------------------------------
    // Hover box
    // -------------------------------------------------------------------------

    private void BuildTip()
    {
        var style = new StyleBoxFlat
        {
            BgColor     = new Color(0.08f, 0.08f, 0.1f, 0.95f),
            BorderColor = new Color(0.6f, 0.6f, 0.65f),
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        style.SetContentMarginAll(8);

        _tip = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 50 };
        _tip.AddThemeStyleboxOverride("panel", style);

        _tipText = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _tipText.AddThemeFontSizeOverride("font_size", 20);
        _tip.AddChild(_tipText);

        AddChild(_tip);
    }

    // Each hover gets a token; leaving the label (or entering again) invalidates the pending show.
    private async Task ShowTipAfterDelayAsync()
    {
        int token = ++_hoverToken;
        try
        {
            if (HoverDelaySeconds > 0 && IsInsideTree())
                await ToSignal(GetTree().CreateTimer(HoverDelaySeconds), SceneTreeTimer.SignalName.Timeout);

            if (token != _hoverToken || !GodotObject.IsInstanceValid(this) || !IsVisibleInTree()) return;

            _tip.Position = new Vector2(0f, Size.Y + 6f); // just under the counter
            _tip.Visible  = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] Floor tracker hover failed: {ex.Message}");
        }
    }

    private void HideTip()
    {
        _hoverToken++;
        if (GodotObject.IsInstanceValid(_tip)) _tip.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_anchor is null || !GodotObject.IsInstanceValid(_anchor))
        {
            _anchor = null;
            if (_gaveUp) return;

            _waited += delta;
            if (!TryAttach() && _waited > GiveUpSeconds)
            {
                _gaveUp = true;
                Log.Warn("[RelayTheSpire] Floor tracker couldn't find NTopBarBossIcon under " +
                         "NRun.Instance.GlobalUi.TopBar; using a fallback position.");
            }
            return;
        }

        // Only re-measure while the icon is shown: in boss rooms the game hides it, and a hidden control
        // can be dropped from its container's layout, which would make its position meaningless.
        if (_anchor.Visible)
            _lastPosition = ComputePosition(_anchor);

        if (_lastPosition is { } pos)
            GlobalPosition = pos;
    }

    // -------------------------------------------------------------------------

    private bool TryAttach()
    {
        Node? topBar = NRun.Instance?.GlobalUi?.TopBar;
        if (topBar is null) return false;

        var icon = topBar.FindChildren("*", "Control", true, false).OfType<NTopBarBossIcon>().FirstOrDefault();
        if (icon is null) return false;

        var host = PickHost(topBar, icon);

        // A normal child of the top bar (not TopLevel): it fades and hides with the rest of the GUI.
        Reparent(host, keepGlobalTransform: false);
        _anchor = icon;

        Log.Info($"[RelayTheSpire] Floor tracker attached to {host.GetType().Name} ({host.GetPath()}), " +
                 $"beside NTopBarBossIcon.");
        return true;
    }

    // Nearest non-Container ancestor of the icon, stopping at the top bar. Containers arrange their children,
    // which would fight our manual placement and push the real top-bar items around.
    private static Node PickHost(Node topBar, Node icon)
    {
        for (Node? n = icon.GetParent(); n is not null; n = n.GetParent())
        {
            if (n is Control and not Container) return n;
            if (ReferenceEquals(n, topBar)) break;
        }
        return topBar;
    }

    private Vector2 ComputePosition(Control icon)
    {
        var scale   = icon.GetGlobalTransform().Scale;
        float right = icon.GlobalPosition.X + icon.Size.X * scale.X;
        float midY  = icon.GlobalPosition.Y + icon.Size.Y * scale.Y / 2f;

        var pos = new Vector2(right + Gap, midY - Size.Y / 2f + VerticalOffset);
        LogLayoutOnce(icon, right, pos);
        return pos;
    }

    private void LogLayoutOnce(Control icon, float right, Vector2 pos)
    {
        if (_loggedLayout) return;
        _loggedLayout = true;

        Log.Info($"[RelayTheSpire] Floor tracker layout: boss icon at ({icon.GlobalPosition.X:F0}, " +
                 $"{icon.GlobalPosition.Y:F0}) size ({icon.Size.X:F0}, {icon.Size.Y:F0}), right edge {right:F0}px; " +
                 $"counter at ({pos.X:F0}, {pos.Y:F0}) size ({Size.X:F0}, {Size.Y:F0}); " +
                 $"host {GetParent()?.GetType().Name}.");
    }
}