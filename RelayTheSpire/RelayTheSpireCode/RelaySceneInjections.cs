using BaseLib.Utils;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using System;

namespace RelayTheSpire.UI;

/// <summary>
/// Injects RelayTheSpire UI nodes into the game's existing scenes using BaseLib's AddedNode.
///
/// Two injection points:
///   1. NMainMenu → RelayMenuPanel (pull UI, always visible on main menu)
///   2. NRun      → TurnPassedNotification (push feedback, shown after map node selection)
/// </summary>
public static class RelaySceneInjections
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sets MouseFilter = Ignore on a node and every Control beneath it.
    /// Containers default to Pass, which still counts as a hit, so an invisible
    /// (transparent) toast would otherwise swallow clicks meant for the game's top bar.
    /// </summary>
    private static void IgnoreMouseRecursive(Node node)
    {
        if (node is Control control)
            control.MouseFilter = Control.MouseFilterEnum.Ignore;

        foreach (var child in node.GetChildren())
            IgnoreMouseRecursive(child);
    }

    // -------------------------------------------------------------------------
    // Main menu → RelayMenuPanel
    // -------------------------------------------------------------------------

    public static readonly AddedNode<NMainMenu, RelayMenuPanel> MainMenuPanel =
        new((mainMenu) =>
        {
            try
            {
                var panel = new RelayMenuPanel();

                panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
                panel.GrowHorizontal = Control.GrowDirection.Begin;
                panel.GrowVertical   = Control.GrowDirection.Begin;

                panel.OffsetRight  = -24f;
                panel.OffsetBottom = -24f;

                mainMenu.AddChild(panel);
                Log.Info("[RelayTheSpire] RelayMenuPanel injected into NMainMenu.");
                return panel;
            }
            catch (Exception ex)
            {
                Log.Error($"[RelayTheSpire] Failed to inject RelayMenuPanel: {ex}");
                var dummy = new RelayMenuPanel();
                mainMenu.AddChild(dummy);
                return dummy;
            }
        });

    // -------------------------------------------------------------------------
    // Run scene → TurnPassedNotification
    // -------------------------------------------------------------------------

    public static readonly AddedNode<NRun, TurnPassedNotification> RunNotification =
        new((nRun) =>
        {
            try
            {
                var notification = new TurnPassedNotification();

                notification.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
                notification.GrowVertical = Control.GrowDirection.End;
                notification.MouseFilter  = Control.MouseFilterEnum.Ignore;

                // Start transparent — Show()/ShowError() will make it visible.
                notification.Modulate = Colors.Transparent;

                // _Ready builds the MarginContainer/VBoxContainer/Labels. Once it has run,
                // make the whole subtree click-through. Subscribed BEFORE AddChild so it
                // fires whether _Ready runs immediately or is deferred until nRun enters the tree.
                notification.Ready += () => IgnoreMouseRecursive(notification);

                nRun.AddChild(notification);

                RelayTheSpireMod.TurnPassed  += notification.Show;
                RelayTheSpireMod.PushFailed  += notification.ShowError;

                notification.TreeExiting += () =>
                {
                    RelayTheSpireMod.TurnPassed  -= notification.Show;
                    RelayTheSpireMod.PushFailed  -= notification.ShowError;
                };

                Log.Info("[RelayTheSpire] TurnPassedNotification injected into NRun.");
                return notification;
            }
            catch (Exception ex)
            {
                Log.Error($"[RelayTheSpire] Failed to inject TurnPassedNotification: {ex}");
                var dummy = new TurnPassedNotification { MouseFilter = Control.MouseFilterEnum.Ignore };
                dummy.Ready += () => IgnoreMouseRecursive(dummy);
                nRun.AddChild(dummy);
                return dummy;
            }
        });
}