using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RelayTheSpire;

/// <summary>A public room as the browser shows it. LastActivity is unix seconds; 0 = the room doesn't exist yet.</summary>
public sealed record RoomBrowseEntry(RoomSettings Room, long LastActivity)
{
    public bool IsCommunity => Room.RoomId == RelayScope.CommunityRoomId;
}

/// <summary>Everything the Rooms tab needs to draw itself. Never touches the network.</summary>
public sealed record RoomBrowseSnapshot(
    RoomBrowseEntry?              Community,
    IReadOnlyList<RoomBrowseEntry> Rooms,   // most recently active first
    bool                          HasMore,  // more candidates left to check
    int                           Failed,   // rooms that couldn't be read
    bool                          Loaded);  // the marker list has been fetched at least once

/// <summary>Plain-words descriptions of a room, shared by the browser and the create dialog.</summary>
public static class RoomText
{
    /// <summary>"5 floors per turn · wait for 2 other turns · aims for 4 runs · lowest floor first"</summary>
    public static string DescribeRules(this RoomSettings room)
    {
        var floors = $"{room.FloorsPerTurn} floor{(room.FloorsPerTurn == 1 ? "" : "s")} per turn";
        var repeat = room.Policy.CooldownTurns is { } n
            ? $"wait for {n} other turn{(n == 1 ? "" : "s")}"
            : "once per run";
        var runs = $"aims for {room.RunCount} run{(room.RunCount == 1 ? "" : "s")}";
        return $"{floors} · {repeat} · {runs} · {room.Order.Describe()}";
    }

    /// <summary>Short plain-words name of a pick order.</summary>
    public static string Describe(this PickOrder order) => order switch
    {
        PickOrder.HighestFloor => "farthest run first",
        PickOrder.Random       => "random run",
        _                      => "lowest floor first",
    };
}

/// <summary>
/// Finds public rooms cheaply. Discovery reads the listed/ folder (one request), then checks each candidate
/// against its own room.json (cached for the session, since it never changes) and its last commit date.
/// Nothing from the marker folder is trusted: a room only appears if its room.json says listed: true.
///
/// Caches (all in memory): room.json forever, marker list 60 s, activity 5 min, details 60 s, and rooms that
/// were rejected (unlisted, missing, stale) are not re-checked for 10 minutes. Every fetch first checks
/// <see cref="GitHubRateLimit"/> so browsing stops while requests are running short.
///
/// One caller at a time: the UI serialises calls. The locks only guard the dictionaries.
/// </summary>
public sealed class RoomBrowser
{
    public const int PageSize = 10;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    private const int ListTtlSeconds           = 60;
    private const int RefreshCooldownSeconds   = 10;
    private const int ActivityTtlSeconds       = 300;
    private const int DetailsTtlSeconds        = 60;
    private const int RejectTtlSeconds         = 600;
    private const int FailureTtlSeconds        = 60;
    private const int MaxConcurrent            = 6;
    private const int MaxBatchesPerCall        = 5; // caps one call at ~50 candidates (~100 requests)

    private readonly GitHubTransport _transport;
    private readonly object _gate = new();

    private List<string> _candidates = new();
    private int  _cursor;
    private long _candidatesAt;      // 0 = never fetched (or invalidated)
    private int  _failed;
    private RoomBrowseEntry? _community;
    private readonly List<RoomBrowseEntry> _entries = new();

    private readonly Dictionary<string, RoomSettings> _rooms = new();
    private readonly Dictionary<string, (long? Value, long At)> _activity = new();
    private readonly Dictionary<string, (RoomStats Stats, long At)> _details = new();
    private readonly Dictionary<string, long> _rejectedUntil = new();

    public RoomBrowser(GitHubTransport transport) => _transport = transport;

    // -------------------------------------------------------------------------
    // Reading state
    // -------------------------------------------------------------------------

    public RoomBrowseSnapshot Snapshot()
    {
        lock (_gate)
            return new RoomBrowseSnapshot(_community, _entries.ToList(), _cursor < _candidates.Count, _failed, _candidatesAt != 0);
    }

    /// <summary>Forces the next <see cref="OpenAsync"/> to re-read the marker list.</summary>
    public void Invalidate()
    {
        lock (_gate) _candidatesAt = 0;
    }

    // -------------------------------------------------------------------------
    // Browsing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shows the first page. Returns what is already loaded if it is fresh (60 s), or, for a forced refresh,
    /// if one happened in the last 10 s. Otherwise re-reads the marker list and Community, and fills a page.
    /// </summary>
    public async Task<RoomBrowseSnapshot> OpenAsync(Func<string, bool> isHidden, bool force)
    {
        var now = RelayManifest.NowUnix();
        long at;
        lock (_gate) at = _candidatesAt;

        if (at != 0 && now - at < (force ? RefreshCooldownSeconds : ListTtlSeconds))
            return Snapshot();

        GuardBudget();
        var ids       = await _transport.ListListedRoomIdsAsync();
        var community = await LoadCommunityAsync(now);

        lock (_gate)
        {
            _candidates   = ids;
            _cursor       = 0;
            _entries.Clear();
            _failed       = 0;
            _community    = community;
            _candidatesAt = now;
        }

        return await LoadMoreAsync(isHidden);
    }

    /// <summary>
    /// Checks candidates until a page of rooms has been added or the candidates run out (at most
    /// <see cref="MaxBatchesPerCall"/> batches, so a repo full of stale rooms can't burn the budget).
    /// Hidden rooms are skipped before any request is made. If a batch fails, the cursor is put back.
    /// </summary>
    public async Task<RoomBrowseSnapshot> LoadMoreAsync(Func<string, bool> isHidden)
    {
        var now   = RelayManifest.NowUnix();
        int added = 0;

        for (int round = 0; round < MaxBatchesPerCall && added < PageSize; round++)
        {
            GuardBudget();

            var batch = new List<string>();
            int before;
            lock (_gate)
            {
                before = _cursor;
                while (_cursor < _candidates.Count && batch.Count < PageSize - added)
                {
                    var id = _candidates[_cursor++];
                    if (isHidden(id) || IsRejected(id, now)) continue;
                    batch.Add(id);
                }
            }
            if (batch.Count == 0) break;

            RoomBrowseEntry?[] found;
            try { found = await LoadBatchAsync(batch, now); }
            catch
            {
                lock (_gate) _cursor = before;
                throw;
            }

            lock (_gate)
            {
                foreach (var entry in found)
                    if (entry is not null) { _entries.Add(entry); added++; }

                var sorted = _entries.OrderByDescending(e => e.LastActivity)
                                     .ThenBy(e => e.Room.RoomId, StringComparer.Ordinal)
                                     .ToList();
                _entries.Clear();
                _entries.AddRange(sorted);
            }
        }

        return Snapshot();
    }

    // -------------------------------------------------------------------------
    // Details (live counts, on demand)
    // -------------------------------------------------------------------------

    public bool TryGetCachedDetails(string roomId, out RoomStats stats)
    {
        lock (_gate)
        {
            if (_details.TryGetValue(roomId, out var d) && RelayManifest.NowUnix() - d.At < DetailsTtlSeconds)
            {
                stats = d.Stats;
                return true;
            }
        }
        stats = default;
        return false;
    }

    /// <summary>Counts a room's runs: one request to list them plus one per run. Cached for 60 s.</summary>
    public async Task<RoomStats> GetDetailsAsync(string roomId, string playerHash)
    {
        var scope = RelayScope.ForRoom(roomId);
        if (TryGetCachedDetails(scope.RoomId, out var cached)) return cached;

        GuardBudget();
        var now   = RelayManifest.NowUnix();
        var runs  = await _transport.ListRunsAsync(scope);
        var stats = RoomStats.Compute(runs, playerHash, now);

        lock (_gate) _details[scope.RoomId] = (stats, now);
        return stats;
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private static void GuardBudget()
    {
        if (GitHubRateLimit.IsLow)
            throw new RelayRateLimitException(GitHubRateLimit.DescribeLow());
    }

    // Call with _gate held.
    private bool IsRejected(string id, long now) =>
        _rejectedUntil.TryGetValue(id, out var until) && until > now;

    private void Reject(string id, long now, int ttlSeconds)
    {
        lock (_gate) _rejectedUntil[id] = now + ttlSeconds;
    }

    private async Task<RoomBrowseEntry?[]> LoadBatchAsync(List<string> ids, long now)
    {
        // Deliberately not disposed: if one task throws (rate limit), the others may still be waiting on it.
        var gate = new SemaphoreSlim(MaxConcurrent);
        var tasks = ids.Select(async Task<RoomBrowseEntry?> (id) =>
        {
            await gate.WaitAsync();
            try { return await LoadCandidateAsync(id, now); }
            finally { gate.Release(); }
        }).ToList();

        return await Task.WhenAll(tasks);
    }

    private async Task<RoomBrowseEntry?> LoadCandidateAsync(string id, long now)
    {
        try
        {
            var room = await GetRoomAsync(id);
            if (room is null || !room.Listed)
            {
                Reject(id, now, RejectTtlSeconds); // missing, or a marker for an unlisted room
                return null;
            }

            var last = await GetActivityAsync(id, now) ?? room.CreatedAt;
            if (now - last > (long)StaleAfter.TotalSeconds)
            {
                Reject(id, now, RejectTtlSeconds);
                return null;
            }

            return new RoomBrowseEntry(room, last);
        }
        catch (RelayRateLimitException) { throw; }
        catch (Exception ex) when (ex is RelayTransportException or ArgumentException)
        {
            // Unreadable or invalid room.json, or a network hiccup. Skip it, and don't retry for a minute.
            lock (_gate) _failed++;
            Reject(id, now, FailureTtlSeconds);
            return null;
        }
    }

    private async Task<RoomBrowseEntry> LoadCommunityAsync(long now)
    {
        var room = await GetRoomAsync(RelayScope.CommunityRoomId);
        if (room is null)
            return new RoomBrowseEntry(RoomSettings.CreateCommunity(), 0); // nobody has started it yet

        var last = await GetActivityAsync(room.RoomId, now) ?? room.CreatedAt;
        return new RoomBrowseEntry(room, last);
    }

    // room.json never changes, so it is cached for the whole session.
    private async Task<RoomSettings?> GetRoomAsync(string id)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(id, out var cached)) return cached;
        }

        var room = await _transport.GetRoomAsync(id);
        if (room is not null)
        {
            lock (_gate) _rooms[id] = room;
        }
        return room;
    }

    private async Task<long?> GetActivityAsync(string id, long now)
    {
        lock (_gate)
        {
            if (_activity.TryGetValue(id, out var c) && now - c.At < ActivityTtlSeconds)
                return c.Value;
        }

        var value = await _transport.GetRoomLastActivityAsync(id);
        lock (_gate) _activity[id] = (value, now);
        return value;
    }
}
