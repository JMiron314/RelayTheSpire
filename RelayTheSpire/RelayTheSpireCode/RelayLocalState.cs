using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayTheSpire;

/// <summary>A room the player has used, for the recent-rooms picker.</summary>
public sealed class RecentRoom
{
    [JsonPropertyName("room_id")]   public string  RoomId   { get; set; } = "";

    /// <summary>The room's name as last seen. Untrusted: it is sanitized on load and on every write.</summary>
    [JsonPropertyName("name")]      public string? Name     { get; set; }

    [JsonPropertyName("last_used")] public long    LastUsed { get; set; }

    /// <summary>"Name · ID", or just the ID when the room has no name.</summary>
    [JsonIgnore]
    public string Label => string.IsNullOrEmpty(Name) ? RoomId : $"{Name} · {RoomId}";
}

/// <summary>One thing the player did in a relay run: passed a turn, or ended the run.</summary>
public sealed class RunHistoryEntry
{
    public const string KindTurn  = "turn";
    public const string KindEnded = "ended";

    [JsonPropertyName("room_id")]      public string  RoomId      { get; set; } = "";
    [JsonPropertyName("run_id")]       public string  RunId       { get; set; } = "";

    /// <summary>"turn" (the player passed the baton) or "ended" (the run was won or lost on their turn).</summary>
    [JsonPropertyName("kind")]         public string  Kind        { get; set; } = KindTurn;

    /// <summary>Position of this turn in the run's chain (1 = the run's first turn).</summary>
    [JsonPropertyName("turn")]         public int     Turn        { get; set; }

    /// <summary>The run's floor count after this turn.</summary>
    [JsonPropertyName("floor")]        public int     Floor       { get; set; }

    [JsonPropertyName("floors_played")] public int    FloorsPlayed { get; set; }

    /// <summary>"won" or "lost" for an "ended" entry; null otherwise.</summary>
    [JsonPropertyName("outcome")]      public string? Outcome     { get; set; }

    [JsonPropertyName("character_id")] public string? CharacterId { get; set; }
    [JsonPropertyName("timestamp")]    public long    Timestamp   { get; set; }
}

/// <summary>
/// The last known state of a run on GitHub, saved so My Runs still shows it after a restart. Built from a
/// <see cref="RunLiveState"/> by Refresh. Everything is re-validated on load; the text is untrusted.
/// </summary>
public sealed class RunSnapshot
{
    [JsonPropertyName("room_id")]    public string RoomId    { get; set; } = "";
    [JsonPropertyName("run_id")]     public string RunId     { get; set; } = "";

    /// <summary>A <see cref="LiveKind"/> name.</summary>
    [JsonPropertyName("kind")]       public string Kind      { get; set; } = "";

    [JsonPropertyName("text")]       public string Text      { get; set; } = "";
    [JsonPropertyName("floor")]      public int    Floor     { get; set; }

    /// <summary>Unix time (UTC) when this state was fetched.</summary>
    [JsonPropertyName("checked_at")] public long   CheckedAt { get; set; }

    public static RunSnapshot From(RunLiveState state, long now) => new()
    {
        RoomId = state.RoomId, RunId = state.RunId, Kind = state.Kind.ToString(),
        Text = state.Text, Floor = state.Floor, CheckedAt = now,
    };

    public RunLiveState? ToState() =>
        Enum.TryParse<LiveKind>(Kind, out var kind) && Enum.IsDefined(kind)
            ? new RunLiveState(RoomId, RunId, kind, Text, Floor)
            : null;
}

/// <summary>One run the player has taken part in, built from the history by <see cref="RelayLocalState.Summarize"/>.</summary>
public sealed record MyRunSummary(
    string  RoomId,
    string  RunId,
    string? CharacterId,
    int     Turns,        // how many turns the player took in this run
    int     FloorsPlayed, // floors across those turns
    int     LastFloor,    // the run's floor count after the player's most recent turn
    string? Outcome,      // "won", "lost" or "ended" if the run ended on the player's turn; otherwise null
    long    FirstPlayed,
    long    LastPlayed)
{
    public bool IsEnded => Outcome is not null;
}

/// <summary>
/// Everything the mod remembers on this machine: the last room typed, recently used rooms, and a history of
/// the turns the player has passed. Stored as relay_state.json (see RelayLocalStore).
///
/// Pure logic, no I/O. The file is treated as untrusted: <see cref="Sanitize"/> re-validates every ID and name
/// after loading, so a hand-edited or damaged file can never put an unsafe string into a repo path or the UI.
/// The Community room is never stored as a "recent room" (a blank Room ID already means Community); it can appear in history.
/// </summary>
public sealed class RelayLocalState
{
    public const int CurrentVersion = 1;
    public const int MaxRooms       = 20;
    public const int MaxRuns        = 200;
    public const int MaxHidden = 200;
    private const int MaxRunIdLength = 64;
    private const int MaxLiveTextLength = 200;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>The Room ID field's last value (a valid room ID, or blank). Kept until the player clears it.</summary>
    [JsonPropertyName("last_room_id")]
    public string? LastRoomId { get; set; } = "";

    /// <summary>Most recently used first.</summary>
    [JsonPropertyName("rooms")]
    public List<RecentRoom> Rooms { get; set; } = new();

    /// <summary>Oldest first, so appending is the common case. Capped at <see cref="MaxRuns"/>.</summary>
    [JsonPropertyName("runs")]
    public List<RunHistoryEntry> Runs { get; set; } = new();

    /// <summary>Last fetched GitHub state of runs in <see cref="Runs"/>, at most one per run.</summary>
    [JsonPropertyName("live")]
    public List<RunSnapshot> Live { get; set; } = new();

    /// <summary>Unix time (UTC) of the last successful My Runs refresh; 0 = never.</summary>
    [JsonPropertyName("last_checked")]
    public long LastChecked { get; set; }

	/// <summary>This install's secret (64 lowercase hex chars). Only ever sent as the Bearer token.</summary>
	[JsonPropertyName("install_secret")]
	public string? InstallSecret { get; set; }
    
    /// <summary>Rooms the player hid in the Rooms tab. Local only; the room itself is unaffected.</summary>
    [JsonPropertyName("hidden_rooms")]
    public List<string> HiddenRooms { get; set; } = new();

    /// <summary>True once the player chose "Don't show this again" on the other-mods warning.</summary>
    [JsonPropertyName("hide_mod_warning")]
    public bool HideModWarning { get; set; }

    // -------------------------------------------------------------------------
    // Rooms
    // -------------------------------------------------------------------------

    /// <summary>Marks a room as just used. Ignores invalid IDs and the Community room. A null name keeps the old one.</summary>
    public void Touch(string roomId, string? name, long now)
    {
        if (!TryRecentRoomId(roomId, out var id)) return;

        var existing = Rooms.FirstOrDefault(r => r.RoomId == id);
        if (existing is null)
        {
            existing = new RecentRoom { RoomId = id };
            Rooms.Add(existing);
        }

        existing.LastUsed = now;
        existing.Name     = RoomSettings.SanitizeName(name) ?? existing.Name;

        Rooms = Rooms.OrderByDescending(r => r.LastUsed).Take(MaxRooms).ToList();
    }

    public bool Forget(string roomId) =>
        RelayScope.TryNormalizeRoomId(roomId, out var id) && Rooms.RemoveAll(r => r.RoomId == id) > 0;

    public RecentRoom? Find(string? roomId) =>
        RelayScope.TryNormalizeRoomId(roomId, out var id) ? Rooms.FirstOrDefault(r => r.RoomId == id) : null;

    private static bool TryRecentRoomId(string? input, out string id) =>
        RelayScope.TryNormalizeRoomId(input, out id) && id != RelayScope.CommunityRoomId;
    
    public bool Hide(string? roomId)
    {
        if (!TryRecentRoomId(roomId, out var id) || HiddenRooms.Contains(id)) return false;

        HiddenRooms.Add(id);
        if (HiddenRooms.Count > MaxHidden) HiddenRooms.RemoveAt(0);
        return true;
    }

    public bool IsHidden(string? roomId) =>
        TryRecentRoomId(roomId, out var id) && HiddenRooms.Contains(id);

    /// <summary>Clears the hidden list. Returns how many were hidden.</summary>
    public int UnhideAll()
    {
        int count = HiddenRooms.Count;
        HiddenRooms.Clear();
        return count;
    }

    // -------------------------------------------------------------------------
    // History
    // -------------------------------------------------------------------------

    /// <summary>Records an entry. The same (room, run, turn, kind) replaces the old entry instead of duplicating it.</summary>
    public void AddRun(RunHistoryEntry entry)
    {
        Runs.RemoveAll(r => r.RoomId == entry.RoomId && r.RunId == entry.RunId &&
                            r.Turn == entry.Turn && r.Kind == entry.Kind);
        Runs.Add(entry);

        if (Runs.Count > MaxRuns)
            Runs.RemoveRange(0, Runs.Count - MaxRuns);
    }

    /// <summary>
    /// Stores freshly fetched states (replacing any earlier state of the same run) and notes when. Snapshots
    /// of runs no longer in the history are dropped, so the list can't outgrow it.
    /// </summary>
    public void SetLive(IEnumerable<RunLiveState> states, long now)
    {
        foreach (var state in states)
        {
            Live.RemoveAll(l => l.RoomId == state.RoomId && l.RunId == state.RunId);
            Live.Add(RunSnapshot.From(state, now));
        }

        var known = Runs.Select(r => (r.RoomId, r.RunId)).ToHashSet();
        Live.RemoveAll(l => !known.Contains((l.RoomId, l.RunId)));
        LastChecked = now;
    }

    /// <summary>The turn just passed, taken from the manifest after RecordTurn.</summary>
    public static RunHistoryEntry TurnEntry(RelayManifest m, long now) => new()
    {
        RoomId       = m.RoomId ?? "",
        RunId        = m.RunId,
        Kind         = RunHistoryEntry.KindTurn,
        Turn         = m.RelayChain.Count,
        Floor        = m.FloorsCompleted,
        FloorsPlayed = m.RelayChain.Count > 0 ? m.RelayChain[^1].FloorsPlayed : 0,
        CharacterId  = m.CharacterId,
        Timestamp    = now,
    };

    /// <summary>The run ending on the player's turn, taken from the manifest after MarkEnded.</summary>
    public static RunHistoryEntry EndedEntry(RelayManifest m, long now)
    {
        var entry = TurnEntry(m, now);
        entry.Kind    = RunHistoryEntry.KindEnded;
        entry.Outcome = m.Status;
        return entry;
    }

    /// <summary>
    /// One summary per run the player has history for, most recently played first. History only records the
    /// player's own turns, so this says what THEY did, not where the run stands now (others may have played since).
    /// </summary>
    public List<MyRunSummary> Summarize() =>
        Runs.GroupBy(e => (e.RoomId, e.RunId))
            .Select(g =>
            {
                // A turn can have both a "turn" and an "ended" entry (same turn number): count it once.
                var floorsPerTurn = g.GroupBy(e => e.Turn).Select(t => t.Max(e => e.FloorsPlayed)).ToList();
                var ended         = g.LastOrDefault(e => e.Kind == RunHistoryEntry.KindEnded);

                return new MyRunSummary(
                    g.Key.RoomId, g.Key.RunId,
                    g.LastOrDefault(e => !string.IsNullOrEmpty(e.CharacterId))?.CharacterId,
                    floorsPerTurn.Count, floorsPerTurn.Sum(), g.Max(e => e.Floor),
                    ended is null ? null : ended.Outcome ?? "ended",
                    g.Min(e => e.Timestamp), g.Max(e => e.Timestamp));
            })
            .OrderByDescending(r => r.LastPlayed)
            .ThenBy(r => r.RunId, StringComparer.Ordinal)
            .ToList();

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Re-validates everything after loading. Entries that fail are dropped, not repaired. Always returns this.
    /// </summary>
    public RelayLocalState Sanitize()
    {
        Version = CurrentVersion;

        LastRoomId = RelayScope.TryNormalizeRoomId(LastRoomId, out var last) ? last : "";

        var seen  = new HashSet<string>();
        var rooms = new List<RecentRoom>();
        foreach (var r in (Rooms ?? new()).Where(r => r is not null).OrderByDescending(r => r.LastUsed))
        {
            if (!TryRecentRoomId(r.RoomId, out var id) || !seen.Add(id)) continue;

            rooms.Add(new RecentRoom { RoomId = id, Name = RoomSettings.SanitizeName(r.Name), LastUsed = r.LastUsed });
            if (rooms.Count >= MaxRooms) break;
        }
        Rooms = rooms;

        var runs = new List<RunHistoryEntry>();
        foreach (var e in (Runs ?? new()).Where(e => e is not null))
        {
            if (!RelayScope.TryNormalizeRoomId(e.RoomId, out var roomId) || !IsSafeRunId(e.RunId)) continue;

            e.RoomId      = roomId;
            e.Kind        = e.Kind == RunHistoryEntry.KindEnded ? RunHistoryEntry.KindEnded : RunHistoryEntry.KindTurn;
            e.Outcome     = e.Outcome is "won" or "lost" ? e.Outcome : null;
            e.CharacterId = RoomSettings.SanitizeName(e.CharacterId);
            runs.Add(e);
        }
        Runs = runs.Count > MaxRuns ? runs.GetRange(runs.Count - MaxRuns, MaxRuns) : runs;

        // Saved live states: keep only valid ones that belong to a run in the history, one per run.
        var known    = Runs.Select(r => (r.RoomId, r.RunId)).ToHashSet();
        var seenLive = new HashSet<(string, string)>();
        var live     = new List<RunSnapshot>();
        foreach (var l in (Live ?? new()).Where(l => l is not null))
        {
            if (!RelayScope.TryNormalizeRoomId(l.RoomId, out var roomId) || !IsSafeRunId(l.RunId)) continue;
            if (!known.Contains((roomId, l.RunId)) || !seenLive.Add((roomId, l.RunId))) continue;
            if (!Enum.TryParse<LiveKind>(l.Kind, out var kind) || !Enum.IsDefined(kind)) continue;
            if (SanitizeText(l.Text, MaxLiveTextLength) is not { } text) continue;

            live.Add(new RunSnapshot
            {
                RoomId = roomId, RunId = l.RunId, Kind = kind.ToString(),
                Text = text, Floor = Math.Max(0, l.Floor), CheckedAt = Math.Max(0, l.CheckedAt),
            });
        }
        Live        = live;
        LastChecked = Math.Max(0, LastChecked);
        
        var hiddenRooms = new List<string>();
        foreach (var h in HiddenRooms ?? new())
        {
            if (TryRecentRoomId(h, out var hid) && !hiddenRooms.Contains(hid))
                hiddenRooms.Add(hid);
            if (hiddenRooms.Count >= MaxHidden) break;
        }
        HiddenRooms = hiddenRooms;

		InstallSecret = InstallSecret is { Length: 64 } s && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
    		? s : null;

        return this;
    }

    /// <summary>Removes control characters and caps the length. Null if nothing usable is left.</summary>
    private static string? SanitizeText(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= max) break;
        }

        var clean = sb.ToString().Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static bool IsSafeRunId(string? runId)
    {
        if (string.IsNullOrEmpty(runId) || runId.Length > MaxRunIdLength) return false;
        foreach (var c in runId)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
        return true;
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

    /// <summary>Null if the text isn't valid JSON. The result is already sanitized.</summary>
    public static RelayLocalState? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<RelayLocalState>(json, SerializerOptions)?.Sanitize(); }
        catch { return null; }
    }
}
