using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayTheSpire;

/// <summary>
/// Companion file that travels alongside current_run.save through the relay chain.
/// Stored as relay_manifest.json in the same directory as the active save.
/// </summary>
public class RelayManifest
{
    public const int CurrentVersion = 1;
    public const string FileName = "relay_manifest.json";

    [JsonPropertyName("relay_version")]
    public int RelayVersion { get; set; } = CurrentVersion;

    /// <summary>The seed from rng.seed in current_run.save — our canonical run identifier.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    /// <summary>The start_time from current_run.save — secondary uniqueness guard.</summary>
    [JsonPropertyName("start_time")]
    public long StartTime { get; set; }

    /// <summary>Schema version from current_run.save at the time the relay was created.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("ascension")]
    public int Ascension { get; set; }

    [JsonPropertyName("character_id")]
    public string CharacterId { get; set; } = "";

    [JsonPropertyName("game_mode")]
    public string GameMode { get; set; } = "";

    /// <summary>
    /// How many map points have been visited so far (length of visited_map_coords).
    /// Each entry here corresponds to one node stepped on in the run.
    /// </summary>
    [JsonPropertyName("floors_completed")]
    public int FloorsCompleted { get; set; }

    /// <summary>Ordered list of players who have taken their turn.</summary>
    [JsonPropertyName("relay_chain")]
    public List<RelayEntry> RelayChain { get; set; } = new();

    /// <summary>Unix timestamp (UTC) when this manifest was last written.</summary>
    [JsonPropertyName("last_updated")]
    public long LastUpdated { get; set; }

    // -------------------------------------------------------------------------
    // Claim (lock) fields
    //
    // A claim marks a run as "someone is currently playing this turn". It is written
    // to GitHub with a conditional PUT (see GitHubTransport.TryClaimAsync), which is
    // what makes it a real lock: only one of several racing players can win.
    //
    // All three are null when the run is unclaimed, and are omitted from the JSON
    // (WhenWritingNull), so manifests written before this feature still load unchanged.
    // -------------------------------------------------------------------------

    /// <summary>Player hash of whoever currently holds the claim. Null = unclaimed.</summary>
    [JsonPropertyName("claimed_by")]
    public string? ClaimedBy { get; set; }

    /// <summary>Unix timestamp (UTC) when the claim was made.</summary>
    [JsonPropertyName("claimed_at")]
    public long? ClaimedAt { get; set; }

    /// <summary>
    /// Unix timestamp (UTC) after which the claim no longer blocks other players.
    /// Stored as an absolute time (not a TTL) so changing the configured TTL later
    /// doesn't retroactively alter claims that already exist.
    /// </summary>
    [JsonPropertyName("claim_expires_at")]
    public long? ClaimExpiresAt { get; set; }

    // -------------------------------------------------------------------------
    // Factory & helpers
    // -------------------------------------------------------------------------

    /// <summary>Current time as unix seconds (UTC). Pass this as `now` to the claim helpers.</summary>
    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Creates a brand-new manifest from a freshly parsed active save.
    /// Call this when a player starts a relay run from scratch (floor 0).
    /// </summary>
    public static RelayManifest CreateNew(ActiveSaveData save)
    {
        return new RelayManifest
        {
            RunId          = save.Seed,
            StartTime      = save.StartTime,
            SchemaVersion  = save.SchemaVersion,
            Ascension      = save.Ascension,
            CharacterId    = save.CharacterId,
            GameMode       = save.GameMode,
            FloorsCompleted = save.VisitedMapCoordsCount,
            LastUpdated    = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    /// <summary>
    /// Appends the current player to the relay chain and updates floor count.
    /// Call this before exporting the relay bundle. Also releases any claim: the turn
    /// is over, so the run becomes available to the next player.
    /// </summary>
    public void RecordTurn(string playerHash, ActiveSaveData updatedSave)
    {
        RelayChain.Add(new RelayEntry
        {
            PlayerHash     = playerHash,
            FloorsPlayed   = updatedSave.VisitedMapCoordsCount - FloorsCompleted,
            TurnStartFloor = FloorsCompleted,
            Timestamp      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        FloorsCompleted = updatedSave.VisitedMapCoordsCount;
        // The game may have migrated the save to a newer schema since this manifest was
        // created; the bundle we're about to push is the new schema, so record that.
        SchemaVersion   = updatedSave.SchemaVersion;
        LastUpdated     = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        ClearClaim();
    }

    /// <summary>
    /// True if the given save belongs to the run this manifest tracks.
    /// start_time is authoritative: rng.seed is NOT stable across a game load/re-save
    /// (a v16 save's seed "X" was observed coming back as "oldX" after migration),
    /// so comparing seeds would wrongly treat a migrated relay run as a new run.
    /// </summary>
    public bool MatchesRun(ActiveSaveData save)
    {
        if (StartTime != 0 && save.StartTime != 0)
            return StartTime == save.StartTime;

        return string.Equals(save.Seed, RunId, StringComparison.Ordinal);
    }

    /// <summary>Returns true if this player has already taken a turn in this relay run.</summary>
    public bool HasPlayerPlayed(string playerHash)
    {
        foreach (var entry in RelayChain)
        {
            if (string.Equals(entry.PlayerHash, playerHash, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
    
    /// <summary>
    /// True if the repeat rule stops this player from taking the next turn. Replaces HasPlayerPlayed
    /// for eligibility checks. The one-argument form uses the configured policy; the other exists
    /// so tests can exercise both policies without touching global state.
    /// </summary>
    public bool IsBlockedFromRepeat(string playerHash) =>
        IsBlockedFromRepeat(playerHash, RepeatPolicy.Current);

    public bool IsBlockedFromRepeat(string playerHash, RepeatPolicy policy) =>
        policy.Blocks(RelayChain, playerHash);

    /// <summary>
    /// True if the most recent chain entry is this player's, i.e. the turn currently in progress
    /// has already been recorded for them. Used to avoid recording/pushing the same turn twice.
    /// Correct under every repeat policy.
    /// </summary>
    public bool HasTakenCurrentTurn(string playerHash) =>
        RelayChain.Count > 0 &&
        string.Equals(RelayChain[^1].PlayerHash, playerHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates that the manifest is consistent with the save file we're about to load.
    /// Returns null on success, or an error message string on failure.
    /// </summary>
    public string? ValidateAgainstSave(ActiveSaveData save)
    {
        if (!MatchesRun(save))
            return $"Save (seed '{save.Seed}', start_time {save.StartTime}) does not match relay run " +
                   $"'{RunId}' (start_time {StartTime}). Wrong save file?";

        if (save.SchemaVersion != SchemaVersion)
            return $"Save schema version changed ({SchemaVersion} → {save.SchemaVersion}). " +
                   "A game update may have changed the save format. Proceed with caution.";

        return null;
    }

    // -------------------------------------------------------------------------
    // Claim rules (pure logic: no I/O, time is always passed in, so it's unit-testable)
    // -------------------------------------------------------------------------

    /// <summary>
    /// True if a claim is recorded AND still in force at `now`.
    /// A claim with no expiry time is treated as expired (defensive: a malformed claim
    /// must never block a run forever). At exactly the expiry second the claim is expired.
    /// </summary>
    public bool IsClaimActive(long now) =>
        !string.IsNullOrEmpty(ClaimedBy) && ClaimExpiresAt is { } expires && expires > now;

    /// <summary>Seconds until the active claim expires, or 0 if there is no active claim.</summary>
    public long ClaimSecondsRemaining(long now) =>
        IsClaimActive(now) ? ClaimExpiresAt!.Value - now : 0;
    
    /// <summary>
    /// True once the player has played at least `floorsPerTurn` floors since the last push.
    /// FloorsCompleted is the floor count at the start of the current turn (it only changes
    /// when a turn is recorded), so this holds across quitting and resuming mid-turn.
    /// </summary>
    public bool IsTurnComplete(int currentFloor, int floorsPerTurn) =>
        currentFloor - FloorsCompleted >= Math.Max(1, floorsPerTurn);

    /// <summary>
    /// Where this player stands with respect to claiming this run. Precedence:
    /// already-played beats everything, then an active claim (yours / someone else's),
    /// otherwise the run is available (unclaimed, or the previous claim expired).
    /// </summary>
    public ClaimStatus CheckClaim(string playerHash, long now)
    {
        if (IsEnded)
            return ClaimStatus.RunEnded;
        
        if (IsBlockedFromRepeat(playerHash))
            return ClaimStatus.AlreadyPlayed;

        if (!IsClaimActive(now))
            return ClaimStatus.Available;

        return string.Equals(ClaimedBy, playerHash, StringComparison.OrdinalIgnoreCase)
            ? ClaimStatus.HeldByYou
            : ClaimStatus.HeldByOther;
    }

    /// <summary>
    /// True if this player may pull the run: it's available, or they already hold the claim
    /// (which lets them re-pull after lost local state or a reinstall and refreshes their claim).
    /// </summary>
    public bool CanBeClaimedBy(string playerHash, long now) =>
        CheckClaim(playerHash, now) is ClaimStatus.Available or ClaimStatus.HeldByYou;

    /// <summary>Stamps a claim for this player. Overwrites any existing (expired or own) claim.</summary>
    public void ApplyClaim(string playerHash, long now, TimeSpan ttl)
    {
        ClaimedBy      = playerHash;
        ClaimedAt      = now;
        ClaimExpiresAt = now + (long)ttl.TotalSeconds;
    }

    /// <summary>Removes any claim. Called automatically by RecordTurn.</summary>
    public void ClearClaim()
    {
        ClaimedBy      = null;
        ClaimedAt      = null;
        ClaimExpiresAt = null;
    }

    // -------------------------------------------------------------------------
    // Serialization
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented        = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static RelayManifest? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RelayManifest>(json, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Player identity
    // -------------------------------------------------------------------------

    /// <summary>
    /// Produces a one-way hash of a player identifier (Steam ID, username, etc.).
    /// We never store the raw ID — only the hash — to give players a degree of privacy.
    /// The salt makes rainbow-table attacks impractical for casual cheating.
    /// </summary>
    public static string HashPlayerId(string rawId, string salt = "sts2-relay-v1")
    {
        var input = Encoding.UTF8.GetBytes(rawId + salt);
        var hash  = SHA256.HashData(input);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    
    public static class RunStatus
    {
        public const string Lost = "lost";
        public const string Won  = "won";
    }

    // in RelayManifest:
    /// <summary>Null = run is still going. "lost"/"won" = run is over and must never be pulled.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonIgnore]
    public bool IsEnded => !string.IsNullOrEmpty(Status);

    /// <summary>
    /// Closes out the run: records the final player's turn (if not already recorded),
    /// stamps the outcome, and releases any claim.
    /// </summary>
    public void MarkEnded(string playerHash, string outcome, int finalFloors)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!HasTakenCurrentTurn(playerHash))
        {
            RelayChain.Add(new RelayEntry
            {
                PlayerHash     = playerHash,
                FloorsPlayed   = Math.Max(0, finalFloors - FloorsCompleted),
                TurnStartFloor = FloorsCompleted,
                Timestamp      = now,
            });
        }

        FloorsCompleted = Math.Max(FloorsCompleted, finalFloors);
        Status          = outcome;
        LastUpdated     = now;
        ClearClaim();
    }
}

/// <summary>A player's standing relative to a run's claim. See RelayManifest.CheckClaim.</summary>
public enum ClaimStatus
{
    /// <summary>Unclaimed, or the previous claim has expired. Anyone who hasn't played may claim it.</summary>
    Available,

    /// <summary>This player holds an active claim (safe to re-pull / refresh).</summary>
    HeldByYou,

    /// <summary>Someone else holds an active claim. Skip this run.</summary>
    HeldByOther,

    /// <summary>The repeat rule blocks this player (see RepeatPolicy). Never claimable by them right now.</summary>
    AlreadyPlayed,
    
    /// <summary>The run is over (lost or won). Nobody can claim it.</summary>
    RunEnded,
}

/// <summary>One player's turn in the relay chain.</summary>
public class RelayEntry
{
    /// <summary>SHA-256 of (Steam ID + salt). Never the raw ID.</summary>
    [JsonPropertyName("player_hash")]
    public string PlayerHash { get; set; } = "";

    /// <summary>Number of map nodes this player visited during their turn.</summary>
    [JsonPropertyName("floors_played")]
    public int FloorsPlayed { get; set; }

    /// <summary>Floor count at the start of this player's turn.</summary>
    [JsonPropertyName("turn_start_floor")]
    public int TurnStartFloor { get; set; }

    /// <summary>Unix timestamp (UTC) when this entry was recorded.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}
