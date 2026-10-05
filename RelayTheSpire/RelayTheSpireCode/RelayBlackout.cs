using Godot;
using MegaCrit.Sts2.Core.Logging;
using System;

namespace RelayTheSpire;

/// <summary>
/// Full-screen black overlay that hides the new floor while a relay push is in progress.
/// Parented to the scene-tree root (not the run scene) so it survives the run being torn down
/// and the main menu loading. Independent of the game's own transition node.
/// </summary>
internal static class RelayBlackout
{
    private static CanvasLayer? _layer;
    private static ColorRect?   _rect;

    public static void Show(string message = "Passing the baton...")
    {
        try
        {
            if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;

            var root = ((SceneTree)Engine.GetMainLoop()).Root;

            _layer = new CanvasLayer { Layer = 128 }; // above everything the game draws
            _rect  = new ColorRect
            {
                Color       = Colors.Black,
                MouseFilter = Control.MouseFilterEnum.Stop, // swallow clicks while we work
            };
            _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);

            var label = new Label
            {
                Text                = message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Modulate            = new Color(1f, 1f, 1f, 0.6f),
            };
            label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            label.AddThemeFontSizeOverride("font_size", 20);
            _rect.AddChild(label);

            _layer.AddChild(_rect);
            root.AddChild(_layer);
        }
        catch (Exception ex)
        {
            Log.Error($"[RelayTheSpire] Failed to show blackout overlay: {ex}");
        }
    }

    public static void Hide()
    {
        try
        {
            var layer = _layer;
            var rect  = _rect;
            _layer = null;
            _rect  = null;

            if (layer is null || !GodotObject.IsInstanceValid(layer)) return;

            if (rect is null || !GodotObject.IsInstanceValid(rect))
            {
                layer.QueueFree();
                return;
            }

            rect.MouseFilter = Control.MouseFilterEnum.Ignore;
            var tween = rect.CreateTween();
            tween.TweenProperty(rect, "modulate:a", 0f, 0.3f);
            tween.TweenCallback(Callable.From(layer.QueueFree));
        }
        catch (Exception ex)
        {
            Log.Error($"[RelayTheSpire] Failed to hide blackout overlay: {ex}");
        }
    }
}