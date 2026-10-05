using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace RelayTheSpire;

/// <summary>
/// Forces the player out of the current run and back to the main menu.
///
/// The teardown mirrors what the game's own "Save &amp; Quit" does (NPauseMenu.CloseToMenu):
///   reset the action queue -> stop run music -> fade out -> RunManager.CleanUp -> NGame.ReturnToMainMenu.
/// The first four calls are the same ones other mods (e.g. QuickRestart) use to tear a run down.
///
/// NGame.ReturnToMainMenu is called by reflection because its exact signature hasn't been
/// verified. We resolve it BEFORE touching anything, so if it's missing we bail out with the
/// run untouched instead of leaving the player on a black screen.
/// </summary>
public static class RelayRunExit
{
    private static bool _exiting;

    /// <summary>Returns true if the exit sequence ran to completion.</summary>
    public static async Task<bool> ToMainMenuAsync(Action? beforeMenu = null)
    {
        if (_exiting) return false;

        var runManager = RunManager.Instance;
        if (runManager is not { IsInProgress: true })
            return false;

        var returnToMenu = ResolveReturnToMainMenu();
        if (returnToMenu is null)
        {
            Log.Error("[RelayTheSpire] NGame.ReturnToMainMenu not found (or needs required arguments) - " +
                      "cannot exit to main menu. Check NGame in ILSpy and update RelayRunExit.");
            return false;
        }

        _exiting = true;
        try
        {
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();
            await NGame.Instance.Transition.FadeOut();
            runManager.CleanUp();
            
            // The run is torn down but the menu isn't built yet, so anything deleted here
            // is already gone when the menu decides whether to show Continue.
            if (beforeMenu != null)
            {
                try { beforeMenu(); }
                catch (Exception ex) { Log.Error($"[RelayTheSpire] beforeMenu hook failed: {ex}"); }
            }

            var args   = returnToMenu.GetParameters().Select(p => p.DefaultValue).ToArray();
            var result = returnToMenu.Invoke(NGame.Instance, args);
            if (result is Task task)
                await task;

            Log.Info("[RelayTheSpire] Returned to main menu after relay push.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[RelayTheSpire] Failed to exit to main menu: {ex}");
            return false;
        }
        finally
        {
            _exiting = false;
        }
    }

    private static MethodInfo? ResolveReturnToMainMenu()
    {
        return typeof(NGame)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ReturnToMainMenu" &&
                                 m.GetParameters().All(p => p.HasDefaultValue));
    }
}
