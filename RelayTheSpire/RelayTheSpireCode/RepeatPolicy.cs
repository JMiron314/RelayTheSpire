using System;
using System.Collections.Generic;

namespace RelayTheSpire;

/// <summary>
/// Decides whether a player who already appears in a run's chain may take another turn.
///   OncePerRun   : anyone in the chain is blocked forever.
///   Cooldown(n)  : a player is blocked only while they are among the last n chain entries.
///                  Cooldown(2) = at least 2 other turns must happen before they can return.
/// Pure logic, no I/O. Cooldown is clamped to >= 1 because 0 would let a player
/// take back-to-back turns.
/// </summary>
public readonly record struct RepeatPolicy(int? CooldownTurns)
{
    /// <summary>Null cooldown = a player may never play the same run twice.</summary>
    public static RepeatPolicy OncePerRun => new(null);

    public static RepeatPolicy Cooldown(int turns) => new(Math.Max(1, turns));

    /// <summary>
    /// The policy used by the game-facing code paths. Set once at startup from the config
    /// (RelayTheSpireMod.Initialize). Tests should prefer the explicit-policy overloads.
    /// </summary>
    public static RepeatPolicy Current { get; set; } = Cooldown(2);

    public bool IsOncePerRun => CooldownTurns is null;

    /// <summary>True if this player may NOT take the next turn of a run with this chain.</summary>
    public bool Blocks(IReadOnlyList<RelayEntry> chain, string playerHash)
    {
        int start = CooldownTurns is { } n ? Math.Max(0, chain.Count - n) : 0;
        for (int i = start; i < chain.Count; i++)
        {
            if (string.Equals(chain[i].PlayerHash, playerHash, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Parses "once" (also never/forever/all) or a positive integer.</summary>
    public static bool TryParse(string? text, out RepeatPolicy policy)
    {
        policy = default;
        var t = text?.Trim().ToLowerInvariant();

        if (t is "once" or "never" or "forever" or "all")
        {
            policy = OncePerRun;
            return true;
        }
        if (int.TryParse(t, out var n) && n >= 1)
        {
            policy = Cooldown(n);
            return true;
        }
        return false;
    }

    /// <summary>Player-facing explanation, for error messages.</summary>
    public string Describe() => CooldownTurns is { } n
        ? $"You can play a run again only after at least {n} other turn{(n == 1 ? "" : "s")} have been played."
        : "Each player may only play a run once.";
}