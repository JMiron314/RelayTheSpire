namespace RelayTheSpire.Tests;

/// <summary>
/// Pure tests for the room browser's logic. No network. Needs RelayScope, RoomSettings, RepeatPolicy,
/// RelayManifest, RelayLocalState, ListedMarker and RoomBrowser.cs compiled into the test project.
/// Add RoomBrowserTests.RunAll() next to the other RunAll() calls in your test Program.
/// </summary>
public static class RoomBrowserTests
{
    public static int RunAll()
    {
        T.Section("Room browser");

        // Marker file names
        T.Check(RelayScope.TryRoomIdFromMarkerName("ABCD2345.json", out var id) && id == "ABCD2345", "reads the room ID from a marker file name");
        T.Check(!RelayScope.TryRoomIdFromMarkerName("abcd2345.json", out _), "rejects a non-canonical (lower-case) name");
        T.Check(!RelayScope.TryRoomIdFromMarkerName("COMMUNITY.json", out _), "Community is never read from the index");
        T.Check(!RelayScope.TryRoomIdFromMarkerName("../x.json", out _) && !RelayScope.TryRoomIdFromMarkerName(".json", out _), "rejects unsafe names");
        T.Check(!RelayScope.TryRoomIdFromMarkerName("ABCD2345.txt", out _) && !RelayScope.TryRoomIdFromMarkerName(null, out _), "rejects other extensions and null");
        T.Check(RelayScope.ListedMarkerPathFor("ABCD2345") == "listed/ABCD2345.json", "marker path");

        // Rules text
        var room = RoomSettings.Create("ABCD2345", 4, 5, RepeatPolicy.Cooldown(2));
        T.Check(room.DescribeRules() == "5 floors per turn · wait for 2 other turns · aims for 4 runs", "rules text for a cooldown room");
        var once = RoomSettings.Create("ABCD2345", 1, 1, RepeatPolicy.OncePerRun);
        T.Check(once.DescribeRules() == "1 floor per turn · once per run · aims for 1 run", "rules text for a once-per-run room");

        // Marker body
        room.Listed = true;
        var marker = ListedMarker.FromJson(ListedMarker.From(room).ToJson());
        T.Check(marker is not null && marker.RoomId == "ABCD2345", "marker survives a JSON round-trip");
        T.Check(ListedMarker.FromJson("{\"room_id\":\"../..\"}") is null && ListedMarker.FromJson("nope") is null, "bad marker bodies are rejected");

        // Hidden rooms
        var state = new RelayLocalState();
        T.Check(state.Hide("abcd2345") && state.IsHidden("ABCD2345"), "hiding normalizes the ID");
        T.Check(!state.Hide("ABCD2345"), "hiding twice changes nothing");
        T.Check(!state.Hide("COMMUNITY") && !state.Hide("x/y"), "Community and unsafe IDs can't be hidden");
        T.Check(RelayLocalState.FromJson(state.ToJson())!.IsHidden("ABCD2345"), "hidden rooms survive a JSON round-trip");
        T.Check(RelayLocalState.FromJson("{\"hidden_rooms\":[\"../x\",\"ABCD2345\",\"ABCD2345\"]}")!.HiddenRooms.Count == 1, "load drops unsafe and duplicate hidden IDs");
        T.Check(state.UnhideAll() == 1 && !state.IsHidden("ABCD2345"), "unhide all");

        return T.Failed;
    }
}
