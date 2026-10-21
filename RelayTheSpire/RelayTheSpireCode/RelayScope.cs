using System;
using System.Security.Cryptography;
using System.Text;

namespace RelayTheSpire;

/// <summary>
/// Where a relay run lives: always inside one room. "Public play" is just the well-known
/// <see cref="CommunityRoomId"/> room, so there is one model, not two.
///
/// A scope is only a location. The rules a room plays by (floors per turn, repeat policy, run target,
/// selection order) live in <see cref="RoomSettings"/>, loaded from room.json.
///
/// Repo layout:
///   rooms/{room_id}/room.json                      (immutable settings)
///   rooms/{room_id}/runs/{run_id}/current_run.save
///   rooms/{room_id}/runs/{run_id}/relay_manifest.json
///
/// Pure logic, no I/O.
/// </summary>
public sealed class RelayScope
{
    public const string RoomsRoot = "rooms";

    /// <summary>
    /// The shared community room. Its ID can never be produced by <see cref="GenerateRoomId"/> (it contains
    /// letters the generator avoids and is not 8 characters long), so it is reserved by construction.
    /// </summary>
    public const string CommunityRoomId = "COMMUNITY";

    // 32 symbols with no 0/O/1/I, so IDs survive being read aloud or retyped. 8 symbols = 40 bits.
    private const string RoomIdAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int RoomIdLength = 8;
    private const int MinRoomIdLength = 4;
    private const int MaxRoomIdLength = 32;

    public string RoomId { get; }

    private RelayScope(string roomId) => RoomId = roomId;

    /// <summary>The scope of a room. The ID is normalized; an unsafe ID throws.</summary>
    public static RelayScope ForRoom(string roomId)
    {
        if (!TryNormalizeRoomId(roomId, out var normalized))
            throw new ArgumentException($"'{roomId}' is not a valid room ID.", nameof(roomId));
        return new RelayScope(normalized);
    }

    public static RelayScope Community { get; } = new(CommunityRoomId);

    public bool IsCommunity => RoomId == CommunityRoomId;

    // -------------------------------------------------------------------------
    // Room IDs
    // -------------------------------------------------------------------------

    /// <summary>A fresh random room ID. Collisions are caught by the create-only write of room.json.</summary>
    public static string GenerateRoomId()
    {
        var chars = new char[RoomIdLength];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = RoomIdAlphabet[RandomNumberGenerator.GetInt32(RoomIdAlphabet.Length)];
        return new string(chars);
    }

    /// <summary>
    /// Cleans up typed or pasted input into a canonical room ID: trims, upper-cases, ignores spaces and
    /// dashes ("abcd-efgh" works). Anything that isn't an ASCII letter or digit is rejected. This matters
    /// for safety, not just tidiness: the ID becomes part of a repo path, so '/', '.' etc. must never get through.
    /// </summary>
    public static bool TryNormalizeRoomId(string? input, out string roomId)
    {
        roomId = "";
        if (input is null) return false;

        var sb = new StringBuilder();
        foreach (var c in input.Trim())
        {
            if (c == '-' || c == ' ') continue;

            if (c >= 'a' && c <= 'z')      sb.Append((char)(c - 'a' + 'A'));
            else if (c >= 'A' && c <= 'Z') sb.Append(c);
            else if (c >= '0' && c <= '9') sb.Append(c);
            else return false;
        }

        if (sb.Length < MinRoomIdLength || sb.Length > MaxRoomIdLength) return false;
        roomId = sb.ToString();
        return true;
    }

    // -------------------------------------------------------------------------
    // Paths (no leading slash; relative to the repo root)
    // -------------------------------------------------------------------------

    public static string RoomFolderFor(string roomId)   => $"{RoomsRoot}/{roomId}";
    public static string RoomJsonPathFor(string roomId) => $"{RoomFolderFor(roomId)}/room.json";
    
    /// <summary>Top-level folder of marker files, one per publicly listed room: listed/{room_id}.json.</summary>
    public const string ListedRoot = "listed";

    public static string ListedMarkerPathFor(string roomId) => $"{ListedRoot}/{roomId}.json";

    /// <summary>
    /// Reads a room ID from a marker file name ("ABCD2345.json"). The name must already be canonical, so one
    /// room can't appear under two names, and Community is refused (it is pinned, never read from the index).
    /// </summary>
    public static bool TryRoomIdFromMarkerName(string? fileName, out string roomId)
    {
        roomId = "";
        if (fileName is null || !fileName.EndsWith(".json", StringComparison.Ordinal)) return false;

        var stem = fileName[..^5];
        if (!TryNormalizeRoomId(stem, out var id) || id != stem || id == CommunityRoomId) return false;

        roomId = id;
        return true;
    }

    /// <summary>Folder holding one subfolder per run.</summary>
    public string RunsRoot => $"{RoomFolderFor(RoomId)}/runs";

    public string RunFolder(string runId)    => $"{RunsRoot}/{runId}";
    public string SavePath(string runId)     => $"{RunFolder(runId)}/current_run.save";
    public string ManifestPath(string runId) => $"{RunFolder(runId)}/relay_manifest.json";
}
