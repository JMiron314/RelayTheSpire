using Godot;
using MegaCrit.Sts2.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;

namespace RelayTheSpire;

/// <summary>
/// Loads and saves relay_state.json (see <see cref="RelayLocalState"/>) and is the one place the rest of the
/// mod talks to it.
///
/// Location: {Godot user data dir}/relay_the_spire/relay_state.json. Deliberately NOT inside the game's saves
/// folders: Steam Cloud sync deletes local files there that aren't in the cloud (see RelaySavePaths), and this
/// is per-machine data anyway. The resolved path is logged at first use.
///
/// Nothing here may ever block play: every failure is logged and swallowed. A file that is damaged is moved
/// aside as .bad; a file that exists but can't be read is left alone and never overwritten.
/// </summary>
internal static class RelayLocalStore
{
    private static readonly object Gate = new();
    private static RelayLocalState? _state;
    private static bool _dirty;
    private static bool _writesDisabled; // the file exists but could not be read: don't overwrite it

    private static string FilePath =>
        Path.Combine(OS.GetUserDataDir(), "relay_the_spire", "relay_state.json");

    // -------------------------------------------------------------------------
    // Access
    // -------------------------------------------------------------------------

    private static RelayLocalState State
    {
        get
        {
            lock (Gate) return _state ??= Load();
        }
    }

    public static string GetLastRoom()
    {
        lock (Gate) return State.LastRoomId ?? "";
    }

    /// <summary>
    /// Remembers the Room ID field's text. Only a valid ID (or blank) is kept, so half-typed text never
    /// replaces a good value. Written on the next <see cref="Flush"/>.
    /// </summary>
    public static void SetLastRoom(string? text)
    {
        var value = RelayScope.TryNormalizeRoomId(text, out var id) ? id : "";
        lock (Gate)
        {
            if (State.LastRoomId == value) return;
            State.LastRoomId = value;
            _dirty = true;
        }
    }

    public static IReadOnlyList<RecentRoom> RecentRooms()
    {
        lock (Gate) return new List<RecentRoom>(State.Rooms);
    }

    /// <summary>The saved GitHub state of each run that has one, keyed by (room ID, run ID).</summary>
    public static IReadOnlyDictionary<(string RoomId, string RunId), RunLiveState> LiveStates()
    {
        lock (Gate)
        {
            var result = new Dictionary<(string RoomId, string RunId), RunLiveState>();
            foreach (var snapshot in State.Live)
                if (snapshot.ToState() is { } state)
                    result[(state.RoomId, state.RunId)] = state;
            return result;
        }
    }

    /// <summary>Unix time of the last successful refresh; 0 if there has never been one.</summary>
    public static long LastChecked()
    {
        lock (Gate) return State.LastChecked;
    }

    /// <summary>Saves states just fetched from GitHub, so they are still there after a restart.</summary>
    public static void SaveLive(IEnumerable<RunLiveState> states)
    {
        try
        {
            lock (Gate)
            {
                State.SetLive(states, RelayManifest.NowUnix());
                _dirty = true;
            }
            Flush();
        }
        catch (Exception ex) { Log.Warn($"[RelayTheSpire] Could not save run states: {ex.Message}"); }
    }

    public static IReadOnlyList<MyRunSummary> MyRuns()
    {
        lock (Gate) return State.Summarize();
    }

    /// <summary>A readable name for a room: Community, its remembered name, or "Room ID".</summary>
    public static string RoomName(string roomId)
    {
        if (roomId == RelayScope.CommunityRoomId) return "Community";
        lock (Gate) return State.Find(roomId)?.Name ?? $"Room {roomId}";
    }

    public static bool IsRecent(string? roomId)
    {
        lock (Gate) return State.Find(roomId) is not null;
    }
    
    public static bool IsHidden(string? roomId)
    {
        lock (Gate) return State.IsHidden(roomId);
    }

    public static int HiddenCount()
    {
        lock (Gate) return State.HiddenRooms.Count;
    }

    public static void Hide(string roomId)
    {
        try
        {
            lock (Gate)
            {
                if (!State.Hide(roomId)) return;
                _dirty = true;
            }
            Flush();
        }
        catch (Exception ex) { Log.Warn($"[RelayTheSpire] Could not hide room: {ex.Message}"); }
    }

    public static void UnhideAll()
    {
        try
        {
            lock (Gate)
            {
                if (State.UnhideAll() == 0) return;
                _dirty = true;
            }
            Flush();
        }
        catch (Exception ex) { Log.Warn($"[RelayTheSpire] Could not unhide rooms: {ex.Message}"); }
    }

    public static bool GetHideModWarning()
    {
        lock (Gate) return State.HideModWarning;
    }

    public static void SetHideModWarning(bool value)
    {
        try
        {
            lock (Gate)
            {
                if (State.HideModWarning == value) return;
                State.HideModWarning = value;
                _dirty = true;
            }
            Flush();
        }
        catch (Exception ex) { Log.Warn($"[RelayTheSpire] Could not save mod warning setting: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // Changes (each one saves immediately)
    // -------------------------------------------------------------------------

    public static void Touch(string roomId, string? name)
    {
        try
        {
            lock (Gate)
            {
                State.Touch(roomId, name, RelayManifest.NowUnix());
                _dirty = true;
            }
            Flush();
        }
        catch (Exception ex) { Log.Warn($"[RelayTheSpire] Could not remember room: {ex.Message}"); }
    }

    public static void Forget(string roomId)
    {
        try
        {
            lock (Gate)
            {
                if (!State.Forget(roomId)) return;
                _dirty = true;
            }
            Flush();
        }
        catch (Exception ex) { Log.Warn($"[RelayTheSpire] Could not forget room: {ex.Message}"); }
    }

    /// <summary>Call after a turn was passed. The manifest must already have had RecordTurn applied.</summary>
    public static void RecordTurn(RelayManifest manifest) => Record(RelayLocalState.TurnEntry(manifest, RelayManifest.NowUnix()), manifest);

    /// <summary>Call after a run ended on the player's turn. The manifest must already have had MarkEnded applied.</summary>
    public static void RecordEnded(RelayManifest manifest) => Record(RelayLocalState.EndedEntry(manifest, RelayManifest.NowUnix()), manifest);

    // Runs on the push path, right before the player is sent to the menu: it must never throw.
    private static void Record(RunHistoryEntry entry, RelayManifest manifest)
    {
        try
        {
            lock (Gate)
            {
                State.AddRun(entry);
                State.Touch(manifest.RoomId ?? "", null, entry.Timestamp); // a room you play in is a room you use
                _dirty = true;
            }
            Flush();
        }
        catch (Exception ex) { Log.Warn($"[RelayTheSpire] Could not record run history: {ex.Message}"); }
    }

    /// <summary>Writes the file if anything changed since the last write.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            if (!_dirty || _state is null) return;
            if (_writesDisabled)
            {
                _dirty = false;
                return;
            }

            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Write beside the file, then swap: a crash mid-write can't leave a half-written state file.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, _state.ToJson());
                File.Move(tmp, path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                // Stay dirty so a later Flush retries.
                Log.Warn($"[RelayTheSpire] Could not save relay_state.json: {ex.Message}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------

    private static RelayLocalState Load()
    {
        string path;
        try { path = FilePath; }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] Could not resolve the relay_state.json path; using in-memory state: {ex.Message}");
            _writesDisabled = true;
            return new RelayLocalState();
        }

        Log.Info($"[RelayTheSpire] Local state file: {path}");
        if (!File.Exists(path))
            return new RelayLocalState();

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] Could not read relay_state.json (leaving it untouched): {ex.Message}");
            _writesDisabled = true;
            return new RelayLocalState();
        }

        var loaded = RelayLocalState.FromJson(text);
        if (loaded is not null)
            return loaded;

        // Not valid JSON. Keep it for inspection, then start fresh.
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
            Log.Warn("[RelayTheSpire] relay_state.json was unreadable; moved it to relay_state.json.bad and started fresh.");
        }
        catch (Exception ex)
        {
            Log.Warn($"[RelayTheSpire] relay_state.json was unreadable and could not be moved aside: {ex.Message}");
            _writesDisabled = true;
        }
        return new RelayLocalState();
    }

	public static string GetInstallSecret()
	{
	    lock (Gate)
    	{
	        if (State.InstallSecret is null)
	        {
	            State.InstallSecret = Convert.ToHexString(
	                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
	            _dirty = true;
	            Flush(); // the lock is reentrant, so this is fine
	        }
	        return State.InstallSecret;
	    }
	}
}
