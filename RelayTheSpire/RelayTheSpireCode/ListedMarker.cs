using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayTheSpire;

/// <summary>
/// The small file written to listed/{room_id}.json when a room is created with "List publicly" ticked.
/// It is a signpost, not the source of truth: the browser discovers rooms from the marker's FILE NAME and
/// then checks the room's own room.json, which must say listed: true. The body is for people browsing
/// the repo, and is validated like every other repo file in case anything ever reads it.
/// </summary>
public sealed class ListedMarker
{
    [JsonPropertyName("room_id")]    public string  RoomId    { get; set; } = "";
    [JsonPropertyName("name")]       public string? Name      { get; set; }
    [JsonPropertyName("created_at")] public long    CreatedAt { get; set; }

    public static ListedMarker From(RoomSettings room) => new()
    {
        RoomId    = room.RoomId,
        Name      = RoomSettings.SanitizeName(room.Name),
        CreatedAt = room.CreatedAt,
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Null if the text isn't valid JSON or names an unusable room. The name is sanitized.</summary>
    public static ListedMarker? FromJson(string json)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<ListedMarker>(json, SerializerOptions);
            if (marker is null || !RelayScope.TryNormalizeRoomId(marker.RoomId, out var id)) return null;

            marker.RoomId = id;
            marker.Name   = RoomSettings.SanitizeName(marker.Name);
            return marker;
        }
        catch { return null; }
    }
}
