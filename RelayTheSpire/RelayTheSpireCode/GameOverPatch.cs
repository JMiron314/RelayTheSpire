using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.IO;
using System.Threading.Tasks;

namespace RelayTheSpire.Patches;

/// <summary>
/// Postfix on NGameOverScreen.Create: the run just ended (death or victory).
/// If it's a relay run, record the final turn, mark the manifest as ended, and push,
/// so nobody can pull the run again.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen.Create))]
internal static class GameOverPatch
{
    private static long _handledStartTime = -1; // guards against Create being called twice

    // Parameter names must match Create's: runState, serializableRun.
    private static void Postfix(RunState runState, SerializableRun serializableRun, NGameOverScreen? __result)
    {
        if (__result is null) return; // test mode

        bool won = runState.CurrentRoom?.IsVictoryRoom ?? false;
        _ = PushRunEnded(runState.TotalFloor, serializableRun.StartTime, won);
    }

    private static async Task PushRunEnded(int totalFloor, long startTime, bool won)
    {
        try
        {
            var controller = RelayTheSpireMod.Controller;
            if (controller is null) return;

            if (startTime != 0 && startTime == _handledStartTime) return;

            var manifestPath = RelaySavePaths.GetFilePath(RelayManifest.FileName);
            if (!File.Exists(manifestPath)) return; // not a relay run

            var manifest = RelayManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
            if (manifest is null) return;

            // Must be THIS run, not a stale manifest from an earlier relay.
            if (startTime != 0 && manifest.StartTime != 0 && manifest.StartTime != startTime)
            {
                Log.Info("[RelayTheSpire] Game over, but local manifest is for a different run - ignoring.");
                return;
            }
            if (manifest.IsEnded) return;

            _handledStartTime = startTime;

            // If the save survived and matches, use it for an accurate floor count and push it too.
            string? saveJson = null;
            int floors = totalFloor;
            var savePath = RelaySavePaths.GetFilePath("current_run.save");
            if (File.Exists(savePath))
            {
                var json  = await File.ReadAllTextAsync(savePath);
                var save  = ActiveSaveData.Parse(json);
                if (save != null && manifest.MatchesRun(save))
                {
                    saveJson = json;
                    floors   = save.VisitedMapCoordsCount;
                }
            }

            var outcome = won ? RelayManifest.RunStatus.Won : RelayManifest.RunStatus.Lost;
            manifest.MarkEnded(RelayTheSpireMod.GetLocalPlayerHash(), outcome, floors);

            // Keep the local copy in sync (through the store, so Steam Cloud agrees).
            await RelaySavePaths.WriteFileAsync(RelayManifest.FileName, manifest.ToJson());

            Log.Info($"[RelayTheSpire] Run {manifest.RunId} ended ({outcome}) at floor {floors}. Pushing...");
            var result = await controller.PushRunEndedAsync(manifest, saveJson);

            if (result.Success)
            {
                RelayTheSpireMod.PendingMenuMessage =
                    $"Run {manifest.RunId} ended ({outcome}) at floor {floors}. " +
                    "It's been recorded on the relay and can't be pulled again.";
                Log.Info("[RelayTheSpire] Run-ended push successful.");
            }
            else
            {
                Log.Error($"[RelayTheSpire] Run-ended push failed: {result.Error}");
                RelayTheSpireMod.PendingMenuMessage = $"Run ended, but the relay push failed: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[RelayTheSpire] Unhandled error during run-ended push: {ex}");
        }
    }
}