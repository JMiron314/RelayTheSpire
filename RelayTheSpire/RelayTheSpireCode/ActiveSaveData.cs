using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System; using System.Linq;

namespace RelayTheSpire;

/// <summary>
/// Lightweight parser for current_run.save.
/// We only deserialize the fields relay mode actually needs —
/// the rest of the file is preserved verbatim when we write it back out.
/// </summary>
public class ActiveSaveData
{
    // -------------------------------------------------------------------------
    // Fields extracted from the save (read-only from our perspective)
    // -------------------------------------------------------------------------

    /// <summary>rng.seed — the canonical run identifier. e.g. "JWH64KK3BX"</summary>
    public string Seed { get; private set; } = "";

    /// <summary>start_time — Unix timestamp when the run began.</summary>
    public long StartTime { get; private set; }

    /// <summary>save_time — Unix timestamp of the last game save.</summary>
    public long SaveTime { get; private set; }

    /// <summary>schema_version — used for compatibility checks across game updates.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>ascension — the difficulty level of this run.</summary>
    public int Ascension { get; private set; }

    /// <summary>players[0].character_id — e.g. "CHARACTER.NECROBINDER"</summary>
    public string CharacterId { get; private set; } = "";

    /// <summary>game_mode — e.g. "standard"</summary>
    public string GameMode { get; private set; } = "";

    /// <summary>
    /// Number of map nodes visited so far (visited_map_coords.Count).
    /// This is our floor counter: each entry = one map point stepped on.
    /// Floor 1 = Neow, Floor 2 = first combat, etc.
    /// </summary>
    public int VisitedMapCoordsCount { get; private set; }

    /// <summary>current_act_index — which act the player is currently in.</summary>
    public int CurrentActIndex { get; private set; }

    /// <summary>
    /// True if pre_finished_room.is_pre_finished is set.
    /// This means the player has completed a room but hasn't collected rewards yet.
    /// A valid hand-off point — the next player will enter the reward screen.
    /// </summary>
    public bool IsPreFinished { get; private set; }

    /// <summary>
    /// The raw JSON text of the save file, preserved for writing back to disk
    /// or pushing to GitHub without any re-serialization loss.
    /// </summary>
    public string RawJson { get; private set; } = "";
    
    public int HistoryFloors { get; private set; }
    public int ActFloors { get; private set; }
    public int PriorActFloors { get; private set; }
    
    // -------------------------------------------------------------------------
    // Parsing
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses a current_run.save file from its raw JSON text.
    /// Returns null if the text is not a valid save file.
    /// </summary>
    public static ActiveSaveData? Parse(string rawJson)
    {
        try
        {
            var root = JsonSerializer.Deserialize<SaveRoot>(rawJson, JsonOpts);
            if (root is null) return null;
            
            int actFloors   = root.VisitedMapCoords?.Count ?? 0;
            int priorFloors = CountHistory(root.MapPointHistory, root.CurrentActIndex);
            int allHistory  = CountHistory(root.MapPointHistory);
            
            return new ActiveSaveData
            {
                RawJson              = rawJson,
                Seed                 = root.Rng?.Seed ?? "",
                StartTime            = root.StartTime,
                SaveTime             = root.SaveTime,
                SchemaVersion        = root.SchemaVersion,
                Ascension            = root.Ascension,
                GameMode             = root.GameMode ?? "",
                CharacterId          = root.Players?.Count > 0
                                          ? root.Players[0].CharacterId ?? ""
                                          : "",
                CurrentActIndex      = root.CurrentActIndex,
                IsPreFinished        = root.PreFinishedRoom?.IsPreFinished ?? false,
                VisitedMapCoordsCount = priorFloors + actFloors,
                HistoryFloors         = allHistory,
                PriorActFloors        = priorFloors,
                ActFloors             = actFloors,
            };
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Validation helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a human-readable description of the current run state.
    /// Shown in the relay UI.
    /// </summary>
    public string Describe()
    {
        var charShort = CharacterId.Replace("CHARACTER.", "");
        return $"{charShort} | Act {CurrentActIndex + 1} | " +
               $"Floor {VisitedMapCoordsCount} | A{Ascension} | Seed: {Seed}";
    }
    
    private static int CountHistory(JsonElement? h, int onlyActsBefore = int.MaxValue)
    {
        if (h is not { ValueKind: JsonValueKind.Array } arr) return 0;
        int total = 0, i = 0;
        foreach (var act in arr.EnumerateArray())
        {
            if (i++ >= onlyActsBefore) break;
            if (act.ValueKind == JsonValueKind.Array) total += act.GetArrayLength();
        }
        return total;
    }
    
    // -------------------------------------------------------------------------
    // DTOs — only the fields we need, everything else is ignored by the deserializer
    // -------------------------------------------------------------------------

    private class SaveRoot
    {
        [JsonPropertyName("rng")]
        public RngData? Rng { get; set; }

        [JsonPropertyName("start_time")]
        public long StartTime { get; set; }

        [JsonPropertyName("save_time")]
        public long SaveTime { get; set; }

        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("ascension")]
        public int Ascension { get; set; }

        [JsonPropertyName("game_mode")]
        public string? GameMode { get; set; }

        [JsonPropertyName("current_act_index")]
        public int CurrentActIndex { get; set; }

        [JsonPropertyName("players")]
        public List<PlayerData>? Players { get; set; }

        [JsonPropertyName("visited_map_coords")]
        public List<object>? VisitedMapCoords { get; set; }

        [JsonPropertyName("pre_finished_room")]
        public PreFinishedRoom? PreFinishedRoom { get; set; }
        
        [JsonPropertyName("map_point_history")]
        public JsonElement? MapPointHistory { get; set; }
    }

    private class RngData
    {
        [JsonPropertyName("seed")]
        public string? Seed { get; set; }
    }

    private class PlayerData
    {
        [JsonPropertyName("character_id")]
        public string? CharacterId { get; set; }
    }

    private class PreFinishedRoom
    {
        [JsonPropertyName("is_pre_finished")]
        public bool IsPreFinished { get; set; }
    }
}
