using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RelayTheSpire.UI;

/// <summary>
/// Warns once per game launch (the first time the main menu appears) if other gameplay-altering mods are
/// loaded. Detection asks the game's ModManager, so only mods that actually loaded are listed.
/// "Don't show this again" is remembered in relay_state.json (RelayLocalStore).
///
/// Static on purpose: RelayMenuPanel is rebuilt after every passed turn, but the warning must only
/// appear on the first main-menu entry of a launch.
/// </summary>
internal static class RelayModCheck
{
    /// <summary>
    /// When true, only loaded mods whose manifest says "affects_gameplay": true are listed, so libraries
    /// (BaseLib) and cosmetic mods don't trigger the warning. Set to false to list every other loaded mod.
    /// </summary>
    private const bool OnlyGameplayAltering = true;

    private static bool _handledThisLaunch;

    /// <summary>
    /// Display names ("Name 1.2.0") of the other mods the game has actually loaded, sorted. Asks the game's
    /// ModManager, so disabled, failed and duplicate mods are not included. Empty if none.
    /// </summary>
    public static IReadOnlyList<string> DetectOtherMods()
    {
        try
        {
            var ownAssembly = typeof(RelayTheSpireMod).Assembly;
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // id -> label

            foreach (var mod in ModManager.GetLoadedMods())
            {
                var manifest = mod.manifest;
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.id)) continue;

                // Skip this mod itself: by assembly, and by id as a fallback.
                if (mod.assemblies.Contains(ownAssembly)) continue;
                if (string.Equals(manifest.id, RelayTheSpire.RelayTheSpireCode.MainFile.ModId,
                                  StringComparison.OrdinalIgnoreCase)) continue;

                if (OnlyGameplayAltering && !manifest.affectsGameplay) continue;

                var label = !string.IsNullOrWhiteSpace(manifest.name) ? manifest.name!.Trim() : manifest.id!;
                if (!string.IsNullOrWhiteSpace(manifest.version)) label += $" {manifest.version!.Trim()}";

                found[manifest.id!] = label;
            }

            return found.Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] Mod detection failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Call from the main-menu panel. Does nothing after the first call of a launch, or if the player
    /// chose "Don't show this again". Never throws.
    /// </summary>
    public static void ShowIfNeeded(Node host)
    {
        if (_handledThisLaunch) return;
        _handledThisLaunch = true;

        try
        {
            if (RelayLocalStore.GetHideModWarning()) return;

            var mods = DetectOtherMods();
            if (mods.Count == 0) return;

            Log.Info($"[RelayTheSpire] Other mods detected: {string.Join(", ", mods)}");

            // Deferred so the panel is fully in the tree before the dialog is added.
            Callable.From(() => ShowDialog(host, mods)).CallDeferred();
        }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] Could not show mod warning: {ex.Message}");
        }
    }

    private static void ShowDialog(Node host, IReadOnlyList<string> mods)
    {
        if (!GodotObject.IsInstanceValid(host) || !host.IsInsideTree()) return;

        var dialog = new AcceptDialog
        {
            Title          = "RelayTheSpire",
            OkButtonText   = "Ok",
            DialogText     = "Warning: additional mods found.\n\n" +
                             string.Join("\n", mods.Select(m => $"  • {m}")) +
                             "\n\nPlaying a Relay Run with gameplay-altering mods may cause unknown interactions. " +
                             "Please only use private rooms and make sure everyone in your group has the same mods ^-^",
            DialogAutowrap = true,
            MinSize        = new Vector2I(460, 0),
        };

        // AddButton puts the extra button to the left of Ok. It does not close the dialog by itself.
        dialog.AddButton("Don't show this again", right: false, action: "dont_show_again");
        dialog.CustomAction += action =>
        {
            if (action == "dont_show_again")
            {
                RelayLocalStore.SetHideModWarning(true);
                dialog.QueueFree();
            }
        };

        dialog.Confirmed += dialog.QueueFree; // Ok
        dialog.Canceled  += dialog.QueueFree; // Esc / window close: ask again next launch

        host.AddChild(dialog);
        dialog.PopupCentered();
    }
}
