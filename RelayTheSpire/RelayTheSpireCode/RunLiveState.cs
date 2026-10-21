using System;

namespace RelayTheSpire;

/// <summary>How a run looks to one player right now. Drives the colour of the "Now:" line in My Runs.</summary>
public enum LiveKind
{
    Missing,        // the run isn't in the repo any more
    Won,
    Lost,
    Ended,          // ended with an unrecognised status
    YouHoldIt,      // you hold its claim
    SomeoneHoldsIt, // another player holds its claim
    CanTake,        // waiting for a player, and you may take it
    Waiting,        // waiting for a player, but the repeat rule stops you for now
    Abandoned,      // started but never passed on, and its claim has expired
}

/// <summary>
/// A run's current state on GitHub, worked out from its manifest for one player at one moment.
/// Pure logic, no I/O: the manifest, the player's hash and the time are all passed in.
/// </summary>
public sealed record RunLiveState(string RoomId, string RunId, LiveKind Kind, string Text, int Floor)
{
    public static RunLiveState Missing(string roomId, string runId) =>
        new(roomId, runId, LiveKind.Missing, "Not found on GitHub (the run or its room may have been removed).", 0);

    public static RunLiveState From(string roomId, string runId, RelayManifest m, string playerHash, long now)
    {
        int floor = m.FloorsCompleted;

        RunLiveState Make(LiveKind kind, string text) => new(roomId, runId, kind, text, floor);

        if (m.IsEnded)
        {
            return m.Status switch
            {
                RelayManifest.RunStatus.Won  => Make(LiveKind.Won,  $"Won at floor {floor}."),
                RelayManifest.RunStatus.Lost => Make(LiveKind.Lost, $"Lost at floor {floor}."),
                _                            => Make(LiveKind.Ended, $"Ended at floor {floor}."),
            };
        }

        if (m.IsClaimActive(now))
        {
            var left = DescribeSpan(m.ClaimSecondsRemaining(now));
            return string.Equals(m.ClaimedBy, playerHash, StringComparison.OrdinalIgnoreCase)
                ? Make(LiveKind.YouHoldIt,      $"You're playing it (floor {floor}). Your claim expires in {left}.")
                : Make(LiveKind.SomeoneHoldsIt, $"Someone is playing it (floor {floor}). Their claim expires in {left}.");
        }

        if (RoomStats.IsAbandoned(m, now))
            return Make(LiveKind.Abandoned, "Started but never passed on. Nobody can take it.");

        if (!m.IsBlockedFromRepeat(playerHash))
            return Make(LiveKind.CanTake, $"Waiting for a player at floor {floor}. You can take it.");

        if (m.Policy.CooldownTurns is not { } cooldown)
            return Make(LiveKind.Waiting, $"Waiting at floor {floor}. Each player may only play this run once.");

        // Blocked while among the last `cooldown` chain entries: count the turns since this player's last one.
        int last = -1;
        for (int i = 0; i < m.RelayChain.Count; i++)
            if (string.Equals(m.RelayChain[i].PlayerHash, playerHash, StringComparison.OrdinalIgnoreCase))
                last = i;

        int remaining = Math.Max(1, cooldown - (m.RelayChain.Count - 1 - last));
        return Make(LiveKind.Waiting,
            $"Waiting at floor {floor}. You can play it again after {remaining} more turn{(remaining == 1 ? "" : "s")}.");
    }

    private static string DescribeSpan(long seconds) =>
        seconds < 7200 ? $"~{Math.Max(1, (seconds + 59) / 60)} min" : $"~{seconds / 3600} h";
}
