namespace RelayTheSpire;

/// <summary>
/// State shared between the main-menu panel and the character-select screen.
/// Survives the main menu being rebuilt (it is rebuilt after every passed turn). The Room ID field's
/// value also survives a game restart, through RelayLocalStore.
/// </summary>
internal static class RelayStartContext
{
    /// <summary>When true, room codes are masked everywhere in the UI. Static so it survives the menu being rebuilt.</summary>
    public static bool CodeHidden { get; set; }

    /// <summary>The room's name, or a placeholder instead of "Room XXXXXXXX" while codes are hidden.</summary>
    public static string NameOf(RoomSettings room) =>
        CodeHidden && RoomSettings.SanitizeName(room.Name) is null ? "Unnamed room" : room.DisplayName;
    
    private static string? _lastRoomId;

    /// <summary>
    /// Text of the main menu's Room ID field. Blank means the Community room.
    /// Loaded from relay_state.json on first use; setting it queues it for saving (RelayLocalStore.Flush).
    /// </summary>
    public static string LastRoomId
    {
        get => _lastRoomId ??= RelayLocalStore.GetLastRoom();
        set
        {
            _lastRoomId = value;
            RelayLocalStore.SetLastRoom(value);
        }
    }

    /// <summary>State of the "Relay run" checkbox on the character-select screen.</summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// A room already resolved by Take the Baton (so embarking doesn't look it up again).
    /// Null when the player ticked the checkbox by hand; then LastRoomId is resolved at embark time.
    /// </summary>
    public static RoomSettings? ArmedRoom { get; private set; }

    public static void Arm(RoomSettings room)
    {
        ArmedRoom = room;
        Enabled   = true;
    }

    /// <summary>Called when character select closes, so an armed room never leaks into a normal run.</summary>
    public static void Clear()
    {
        ArmedRoom = null;
        Enabled   = false;
    }

    /// <summary>Short description of where an embarked relay run would go.</summary>
    public static string DescribeTarget()
    {
        if (ArmedRoom is { } room) return NameOf(room);
        if (string.IsNullOrWhiteSpace(LastRoomId)) return "Community";
        return RelayScope.TryNormalizeRoomId(LastRoomId, out var id)
            ? RelayLocalStore.RoomName(id)   // remembered name, or "Room ID" (masked when hidden, see below)
            : "(invalid room ID)";
    }
}
