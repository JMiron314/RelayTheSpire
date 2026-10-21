using System.Collections.Generic;

namespace RelayTheSpire;

/// <summary>
/// A snapshot of how busy a room is, computed from its run manifests. Shown in the "X ongoing, Y finished"
/// offer, and used to decide whether Take the Baton should start a new run (see RoomSettings.ShouldAutoStart).
///
///   Ended      the run is over (won or lost).
///   Abandoned  a run that was started but never pushed, and whose creator's claim has expired: it can
///              never be played (there is no save to download) so it doesn't count as a live run.
///   Ongoing    everything else that isn't ended.
///   Available  how many runs THIS player could take right now.
/// </summary>
public readonly record struct RoomStats(int Total, int Ongoing, int Ended, int Abandoned, int Available)
{
    public static bool IsAbandoned(RelayManifest manifest, long now) =>
        !manifest.IsEnded && manifest.RelayChain.Count == 0 && !manifest.IsClaimActive(now);

    public static RoomStats Compute(IEnumerable<RunListing> runs, string playerHash, long now)
    {
        int total = 0, ended = 0, abandoned = 0, available = 0;

        foreach (var run in runs)
        {
            total++;
            var m = run.Manifest;

            if (m.IsEnded)                   ended++;
            else if (IsAbandoned(m, now))    abandoned++;

            if (RoomSettings.IsOfferable(m, playerHash, now))
                available++;
        }

        return new RoomStats(total, total - ended - abandoned, ended, abandoned, available);
    }
}
