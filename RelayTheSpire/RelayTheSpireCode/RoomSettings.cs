using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayTheSpire;

/// <summary>Which run Take the Baton prefers within a room.</summary>
public enum PickOrder
{
    /// <summary>Least-advanced run first. Keeps a group's runs balanced. Ties go to the run that has waited longest.</summary>
    LowestFloor,
    /// <summary>Farthest-along run first. Pushes one run as deep as possible. Ties go to the most recently updated.</summary>
    HighestFloor,
    /// <summary>A random run among those the player could take.</summary>
    Random,
}

/// <summary>
/// A room: its identity, visibility and the rules everyone in it plays by. Stored once as
/// rooms/{room_id}/room.json (create-only, never edited), so the rules can't drift.
///
/// Every lobby is a room. The Community room (see <see cref="CreateCommunity"/>) is just a listed room with
/// well-known defaults; a private room is an unlisted one whose ID is shared with friends.
///
/// The run count is a SOFT target: below it Take the Baton starts new runs on its own; at or above it the
/// player is asked first. It is never a hard cap. See <see cref="ShouldAutoStart"/>.
/// </summary>
public class RoomSettings
{
    public const int CurrentVersion = 1;

    public const int MaxRunCount      = 50;
    public const int MaxFloorsPerTurn = 50;
    public const int MaxNameLength    = 32;

    public const int DefaultRunCount      = 4;
    public const int DefaultFloorsPerTurn = 5;
    public static readonly RepeatPolicy DefaultPolicy = RepeatPolicy.Cooldown(2);
    public static readonly TimeSpan DefaultClaimTtl   = TimeSpan.FromHours(12);
    

    [JsonPropertyName("room_version")]
    public int RoomVersion { get; set; } = CurrentVersion;

    /// <summary>The room's ID (also its folder name). Stored redundantly as a sanity check.</summary>
    [JsonPropertyName("room_id")]
    public string RoomId { get; set; } = "";

    /// <summary>Optional human-friendly name. Untrusted text: always show <see cref="DisplayName"/>, never this directly.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Whether the room appears in the lobby browser. Unlisted rooms are joined by ID only.</summary>
    [JsonPropertyName("listed")]
    public bool Listed { get; set; }

    /// <summary>
    /// How many runs the room aims to have going. A soft target (see <see cref="ShouldAutoStart"/>).
    /// </summary>
    [JsonPropertyName("run_count")]
    public int RunCount { get; set; } = DefaultRunCount;

    /// <summary>
    /// False (private-room default): the target counts every run ever started, so ended runs are NOT replaced
    /// and the room winds down. True (Community): the target counts only live runs, so ended runs are replaced.
    /// </summary>
    [JsonPropertyName("replace_finished")]
    public bool ReplaceFinished { get; set; }

    [JsonPropertyName("floors_per_turn")]
    public int FloorsPerTurn { get; set; } = DefaultFloorsPerTurn;

    /// <summary>"once" or a cooldown number. See <see cref="RepeatPolicy"/>.</summary>
    [JsonPropertyName("repeat_policy")]
    public string RepeatPolicyText { get; set; } = DefaultPolicy.ToText();

    /// <summary>"lowest", "highest" or "random". Anything else is read as "lowest".</summary>
    [JsonPropertyName("pick_order")]
    public string PickOrderText { get; set; } = "lowest";

    [JsonPropertyName("claim_ttl_seconds")]
    public long ClaimTtlSeconds { get; set; } = (long)DefaultClaimTtl.TotalSeconds;

    /// <summary>Unix timestamp (UTC) when the room was created.</summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonIgnore]
    public RepeatPolicy Policy
    {
        get => RepeatPolicy.TryParse(RepeatPolicyText, out var p) ? p : RepeatPolicy.OncePerRun;
        set => RepeatPolicyText = value.ToText();
    }

    [JsonIgnore]
    public PickOrder Order
    {
        get => PickOrderText?.Trim().ToLowerInvariant() switch
        {
            "highest" => PickOrder.HighestFloor,
            "random"  => PickOrder.Random,
            _         => PickOrder.LowestFloor,
        };
        set => PickOrderText = value switch
        {
            PickOrder.HighestFloor => "highest",
            PickOrder.Random       => "random",
            _                      => "lowest",
        };
    }

    [JsonIgnore]
    public TimeSpan ClaimTtl => TimeSpan.FromSeconds(ClaimTtlSeconds);

    // -------------------------------------------------------------------------
    // Display
    // -------------------------------------------------------------------------

    /// <summary>
    /// The room's name, made safe to show: control characters removed, whitespace collapsed, length capped.
    /// Falls back to "Room {id}" when there is no usable name. room.json comes from the repo, so the name is
    /// never trusted as written.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => SanitizeName(Name) ?? $"Room {RoomId}";

    /// <summary>Cleans a player-entered or repo-supplied name. Null if nothing usable is left.</summary>
    public static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var sb = new StringBuilder();
        bool pendingSpace = false;
        foreach (var c in name)
        {
            if (char.IsControl(c)) continue;
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }

            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
            if (sb.Length >= MaxNameLength) break;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Creation & recommendations
    // -------------------------------------------------------------------------

    /// <summary>A private (unlisted) room, as made by the create-room panel.</summary>
    public static RoomSettings Create(string roomId, int runCount, int floorsPerTurn, RepeatPolicy policy, string? name = null) => new()
    {
        RoomId        = roomId,
        Name          = SanitizeName(name),
        RunCount      = runCount,
        FloorsPerTurn = floorsPerTurn,
        Policy        = policy,
        CreatedAt     = RelayManifest.NowUnix(),
    };

    /// <summary>
    /// The Community room's fixed settings. Everyone's mod creates the room from these if it doesn't exist
    /// yet; whoever wins the create-only write defines it for good. Multi-floor turns with a short cooldown,
    /// farthest-along run first, and finished runs are replaced so there is always something to play.
    /// </summary>
    public static RoomSettings CreateCommunity() => new()
    {
        RoomId          = RelayScope.CommunityRoomId,
        Name            = "Community",
        Listed          = true,
        RunCount        = 10,
        ReplaceFinished = true,
        FloorsPerTurn   = 5,
        Policy          = RepeatPolicy.Cooldown(2),
        Order           = PickOrder.HighestFloor,
        CreatedAt       = RelayManifest.NowUnix(),
    };

    /// <summary>A copy of these settings under a different room ID (used when an ID collides on creation).</summary>
    public RoomSettings WithRoomId(string roomId)
    {
        var copy = (RoomSettings)MemberwiseClone();
        copy.RoomId = roomId;
        return copy;
    }

    /// <summary>
    /// The suggestion shown in the create-room panel for a group of this size:
    /// runs = players + 1 (a spare so nobody waits), cooldown = players - 1 (you wait for everyone else).
    /// </summary>
    public static (int RunCount, RepeatPolicy Policy) Recommend(int players)
    {
        players = Math.Max(1, players);
        return (players + 1, RepeatPolicy.Cooldown(players - 1)); // Cooldown clamps to >= 1
    }

    // -------------------------------------------------------------------------
    // Rules
    // -------------------------------------------------------------------------

    /// <summary>Copies this room's identity and rules onto a manifest being created.</summary>
    public void StampRules(RelayManifest manifest)
    {
        manifest.RoomId        = RoomId;
        manifest.FloorsPerTurn = Math.Max(1, FloorsPerTurn);
        manifest.Policy        = Policy;
    }

    /// <summary>How many runs count toward the target: live ones only if finished runs are replaced, else every real run.</summary>
    public int CountTowardTarget(RoomStats stats) =>
        ReplaceFinished ? stats.Ongoing : stats.Total - stats.Abandoned;

    /// <summary>
    /// True if Take the Baton should start a new run by itself (the room is below its target). At or above the
    /// target the player is asked instead, so the target is a nudge, never a cap. Two players acting at the
    /// same moment can overshoot it by one, which is harmless.
    /// </summary>
    public bool ShouldAutoStart(RoomStats stats) => CountTowardTarget(stats) < RunCount;

    /// <summary>
    /// True if this player could be handed this run right now: it has been pushed at least once (an empty
    /// chain is a just-started run whose first save was never pushed, so there is nothing to download), it
    /// hasn't ended, the player isn't blocked by the run's repeat rule, and no one else holds an active claim.
    /// The run's own recorded rules decide, via <see cref="RelayManifest.CheckClaim"/>.
    /// </summary>
    public static bool IsOfferable(RelayManifest manifest, string playerHash, long now) =>
        manifest.RelayChain.Count > 0 &&
        manifest.CheckClaim(playerHash, now) is ClaimStatus.Available or ClaimStatus.HeldByYou;

    /// <summary>
    /// Picks the run this player should take within this room, or null if none qualifies. Ordering follows
    /// <see cref="Order"/>; for floor-based orders, remaining ties break on run ID so the result is
    /// deterministic. For <see cref="PickOrder.Random"/> the choice is uniform among eligible runs
    /// (pass <paramref name="rng"/> for a reproducible result in tests).
    /// </summary>
    public RunListing? SelectRun(IEnumerable<RunListing> runs, string playerHash, long now, Random? rng = null)
    {
        var eligible = runs.Where(r => IsOfferable(r.Manifest, playerHash, now));

        if (Order == PickOrder.Random)
        {
            // Sort by run ID first so a seeded Random gives a stable result regardless of listing order.
            var list = eligible.OrderBy(r => r.RunId, StringComparer.Ordinal).ToList();
            return list.Count == 0 ? null : list[(rng ?? Random.Shared).Next(list.Count)];
        }

        var ordered = Order == PickOrder.HighestFloor
            ? eligible.OrderByDescending(r => r.Manifest.FloorsCompleted)
                .ThenByDescending(r => r.Manifest.LastUpdated)
                .ThenBy(r => r.RunId, StringComparer.Ordinal)
            : eligible.OrderBy(r => r.Manifest.FloorsCompleted)
                .ThenBy(r => r.Manifest.LastUpdated)
                .ThenBy(r => r.RunId, StringComparer.Ordinal);

        return ordered.FirstOrDefault();
    }

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    /// <summary>The first thing wrong with these settings, or null if they are usable.</summary>
    public string? Validate()
    {
        if (!RelayScope.TryNormalizeRoomId(RoomId, out _))
            return "The room ID is invalid.";
        if (RunCount < 1 || RunCount > MaxRunCount)
            return $"Number of runs must be between 1 and {MaxRunCount}.";
        if (FloorsPerTurn < 1 || FloorsPerTurn > MaxFloorsPerTurn)
            return $"Floors per turn must be between 1 and {MaxFloorsPerTurn}.";
        if (!RepeatPolicy.TryParse(RepeatPolicyText, out _))
            return "Repeat policy must be 'once' or a number of 1 or more.";
        if (ClaimTtlSeconds <= 0)
            return "Claim time must be positive.";
        return null;
    }

    /// <summary>Problems that don't make the room unusable. Shown to the creator, never blocking.</summary>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();

        if (Policy.CooldownTurns is { } n && n > RunCount)
            warnings.Add($"A cooldown of {n} with only {RunCount} run{(RunCount == 1 ? "" : "s")} means " +
                         "players will often have nothing to play and will have to wait or start a new run.");

        return warnings;
    }

    // -------------------------------------------------------------------------
    // Serialization
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Null if the text isn't valid JSON. Call <see cref="Validate"/> before trusting the values.</summary>
    public static RoomSettings? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<RoomSettings>(json, SerializerOptions); }
        catch { return null; }
    }
}
