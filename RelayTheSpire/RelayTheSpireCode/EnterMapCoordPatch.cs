using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Saves;

namespace RelayTheSpire.Patches;

/// <summary>
/// Postfix patch on RunManager.EnterMapCoord.
///
/// This fires after the player selects a map node and the game has:
///   1. Added the new coord to VisitedMapCoords
///   2. Called SaveManager.SaveRun(null) - writing current_run.save for the new floor
///   3. Entered the new room
///
/// By patching here rather than TravelToMapCoord, we capture the save that represents
/// the START of the next floor - exactly what the next relay player needs to pick up from.
///
/// We only push to GitHub if:
///   - The relay controller is configured
///   - A relay manifest exists for this run (i.e. this is a relay run, not a normal run)
///   - The current player has NOT already taken their turn in this run
///
/// Ordering guarantee (push-then-commit):
///   The turn is recorded on a working copy of the manifest, pushed to GitHub, and only THEN
///   written to the local manifest. If the push fails, the local manifest is untouched, so the
///   player is still "unrecorded" and the next map-node selection retries the push cleanly.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
internal static class EnterMapCoordPatch
{
    
    // Runs BEFORE the game enters the new node. If this selection is going to pass the turn
    // and kick the player to the menu, cover the screen now so the new floor is never seen.
    private static void Prefix(out bool __state)
    {
        __state = false;
        try
        {
            if (WillPassTurn())
            {
                RelayBlackout.Show();
                __state = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] Blackout pre-check failed (continuing normally): {ex.Message}");
        }
    }
    
    /// <summary>
    /// Cheap synchronous prediction of "this node selection will push and exit to the menu".
    /// At prefix time current_run.save is still the PREVIOUS floor, which is fine: we only use it
    /// to confirm the run matches the manifest. A false positive is harmless because the overlay
    /// is always removed in PushAfterMapCoordEntered's finally block.
    /// </summary>
    private static bool WillPassTurn()
    {
        var controller = RelayTheSpireMod.Controller;
        if (controller is null || !controller.ExitToMenuAfterPush) return false;

        var manifestPath = RelaySavePaths.GetFilePath(RelayManifest.FileName);
        var savePath     = RelaySavePaths.GetFilePath("current_run.save");
        if (!File.Exists(manifestPath) || !File.Exists(savePath)) return false;

        var manifest = RelayManifest.FromJson(File.ReadAllText(manifestPath));
        var save     = ActiveSaveData.Parse(File.ReadAllText(savePath));
        if (manifest is null || save is null || !manifest.MatchesRun(save) || manifest.IsEnded) return false;
        
        var playerHash = RelayTheSpireMod.GetLocalPlayerHash();
        if (manifest.HasTakenCurrentTurn(playerHash) ||
            PushedThisSession.Contains((manifest.StartTime, manifest.RelayChain.Count)))
            return false;

        // The save is still the previous floor; the selection about to happen adds one more.
        return manifest.IsTurnComplete(save.VisitedMapCoordsCount + 1, controller.FloorsPerTurn);
    }
    
    // 0 = idle, 1 = a push is running. Prevents overlapping pushes if EnterMapCoord fires
    // again while a previous push is still awaiting the network.
    private static int _pushInFlight;

    // Safety net for the case where the push succeeded but the LOCAL manifest write failed:
    // the local file would still lack this player, so without this we'd push a duplicate turn
    // on the next node. Keyed by start_time (the stable run identity; see MatchesRun).
    private static readonly HashSet<(long StartTime, int ChainLength)> PushedThisSession = new();

    private static void Postfix(Task __result, bool __state)
    {
        _ = PushAfterMapCoordEntered(__result, __state);
    }

    private static async Task PushAfterMapCoordEntered(Task enterMapCoordTask, bool blackedOut)
    {
        try
        {
            try
            {
                await enterMapCoordTask;
            }
            catch (Exception ex)
            {
                Log.Error($"[RelayTheSpire] EnterMapCoord task faulted before relay push: {ex}");
                return;
            }

            var controller = RelayTheSpireMod.Controller;
            if (controller is null)
                return;

            if (Interlocked.CompareExchange(ref _pushInFlight, 1, 0) != 0)
            {
                Log.Info("[RelayTheSpire] A relay push is already in flight - skipping.");
                return;
            }

            try
            {
                await TryPushAsync(controller);
            }
            catch (Exception ex)
            {
                Log.Error($"[RelayTheSpire] Unhandled error during relay push: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _pushInFlight, 0);
            }
        }
        finally
        {
            if (blackedOut)
                RelayBlackout.Hide(); // menu is up (or we're staying in the run): reveal it
        }
    }

    private static async Task TryPushAsync(RelayController controller)
    {
        // Resolve paths via RelaySavePaths - the single source of truth that matches
        // what the game's HasRunSave checks (includes SavesDir; see RelayController.cs).
        var savePath = RelaySavePaths.GetFilePath("current_run.save");

        if (!File.Exists(savePath))
        {
            Log.Warn("[RelayTheSpire] current_run.save not found after EnterMapCoord - skipping relay push. " +
                     RelaySavePaths.Diagnose("current_run.save"));
            return;
        }

        if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
            await pending;
        var saveJson = await File.ReadAllTextAsync(savePath);
        var save     = ActiveSaveData.Parse(saveJson);
        if (save is null)
        {
            Log.Warn("[RelayTheSpire] Could not parse current_run.save - skipping relay push.");
            return;
        }
        
        // Load the relay manifest. If none exists, this is not a relay run.
        var manifestPath = RelaySavePaths.GetFilePath(RelayManifest.FileName);
        if (!File.Exists(manifestPath))
            return; // Normal play - do nothing.

        var manifest = RelayManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
        if (manifest is null || !manifest.MatchesRun(save))
        {
            Log.Info("[RelayTheSpire] Manifest unreadable or belongs to a different run " +
                     "(stale from an earlier relay?) - treating as a normal run, no push.");
            return;
        }

        var playerHash = RelayTheSpireMod.GetLocalPlayerHash();
        Log.Info($"[RelayTheSpire] floors: prior={save.PriorActFloors} coords={save.ActFloors} " +
                 $"history={save.HistoryFloors} used={save.VisitedMapCoordsCount} manifestFloors={manifest.FloorsCompleted}");

        // ---- Already-pushed guard -------------------------------------------------------
        // One push per turn: either the chain already ends with this player, or we already pushed
        // this exact turn this session (covers a failed local manifest write).
        var sessionKey = (manifest.StartTime, manifest.RelayChain.Count);
        if (manifest.HasTakenCurrentTurn(playerHash) || PushedThisSession.Contains(sessionKey))
        {
            Log.Info($"[RelayTheSpire] Turn already passed for run {manifest.RunId} - skipping push.");
            return;
        }
        
        // ---- Turn-length guard ----------------------------------------------------------
        // Not enough floors played yet: keep playing. Nothing is written or pushed.
        if (!manifest.IsTurnComplete(save.VisitedMapCoordsCount, controller.FloorsPerTurn))
        {
            Log.Info($"[RelayTheSpire] Floor {save.VisitedMapCoordsCount}: " +
                     $"{save.VisitedMapCoordsCount - manifest.FloorsCompleted}/{controller.FloorsPerTurn} " +
                     "floors played this turn - not passing yet.");
            return;
        }

        // ---- Record the turn on the in-memory copy ONLY ---------------------------------
        // `manifest` was freshly deserialized above, so mutating it does not touch the
        // local file. Nothing is written locally until the push succeeds.
        manifest.RecordTurn(playerHash, save);

        Log.Info($"[RelayTheSpire] Pushing relay bundle for run {manifest.RunId}, " +
                 $"floor {save.VisitedMapCoordsCount}...");

        var result = await controller.PushRelayAsync(savePath, manifest);

        if (!result.Success)
        {
            // Local manifest is unchanged, so the next node selection will retry.
            Log.Error($"[RelayTheSpire] Relay push failed: {result.Error}");
            RelayTheSpireMod.OnPushFailed(result.Error ?? "Unknown error");
            return;
        }

        // ---- Push succeeded: commit the turn locally ------------------------------------
        PushedThisSession.Add(sessionKey);

        try
        {
            await RelaySavePaths.WriteFileAsync(RelayManifest.FileName, manifest.ToJson());
        }
        catch (Exception ex)
        {
            // The remote is already correct. PushedThisSession prevents a duplicate push.
            Log.Error($"[RelayTheSpire] Push succeeded but local manifest write failed: {ex}");
        }

        Log.Info($"[RelayTheSpire] Relay push successful. Turn {manifest.RelayChain.Count} recorded.");
        RelayTheSpireMod.OnTurnPassed(save, manifest);

        // It's no longer this player's turn - send them to the main menu.
        // (Testing behaviour; eventually we'll block Continue instead.)
        if (controller.ExitToMenuAfterPush)
        {
            RelayTheSpireMod.PendingMenuMessage =
                $"Turn passed! Floor {save.VisitedMapCoordsCount} pushed to the relay " +
                $"(turn {manifest.RelayChain.Count}).\nRun ID: {manifest.RunId}";

            if (SaveManager.Instance.CurrentRunSaveTask is { } pendingSave)
                await pendingSave;
            
            if (!await RelayRunExit.ToMainMenuAsync(RelaySavePaths.RemoveLocalRun))
                RelayTheSpireMod.PendingMenuMessage = null;
            }
    }
}