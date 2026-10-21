using Godot;
using MegaCrit.Sts2.Core.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RelayTheSpire.UI;

/// <summary>
/// The "Rooms" tab of the main-menu panel: public rooms, Community pinned at the top, the rest most recently
/// active first. All data comes from RoomBrowser (RelayController.Browser), which caches, so rebuilding this
/// view after a turn passes costs nothing. Nothing is fetched until the tab is opened (<see cref="OnOpened"/>).
///
/// Join does not take the baton: it raises <see cref="JoinRequested"/> and the panel fills in the Room ID
/// field and switches to Play. Details loads the live run counts for one room on demand.
/// </summary>
public partial class RelayRoomsView : VBoxContainer
{
    private static readonly Color MutedColor = new(0.8f, 0.8f, 0.8f);
    private static readonly Color GoodColor  = new(0.7f, 1f, 0.7f);
    private static readonly Color WarnColor  = new(1f, 0.75f, 0.3f);

    // Transient, so a rebuilt panel still knows a load is running and how the last one went.
    private static bool    Busy;
    private static string? LastNote;

    /// <summary>The player pressed Join on a room (Community included).</summary>
    public event Action<RoomSettings>? JoinRequested;

    private Label         _status = null!;
    private Button        _refresh = null!;
    private Button        _unhide = null!;
    private Button        _more = null!;
    private VBoxContainer _rows = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);

        var top = new HBoxContainer();
        AddChild(top);

        _status = new Label
        {
            AutowrapMode        = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize   = new Vector2(200, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _status.AddThemeFontSizeOverride("font_size", 13);
        top.AddChild(_status);

        _unhide = new Button
        {
            Text        = "Unhide all",
            Visible     = false,
            TooltipText = "Show the rooms you hid again.",
        };
        _unhide.Pressed += OnUnhidePressed;
        top.AddChild(_unhide);

        _refresh = new Button
        {
            Text        = "Refresh",
            TooltipText = "Look for new public rooms.",
        };
        _refresh.Pressed += () => _ = LoadAsync(force: true);
        top.AddChild(_refresh);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize    = new Vector2(360, 280),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        AddChild(scroll);

        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_rows);

        _more = new Button { Text = "Load more", Visible = false };
        _more.Pressed += () => _ = LoadAsync(force: false, more: true);
        AddChild(_more);

        Rebuild();
    }

    /// <summary>Called by the panel when the tab is opened. Cheap if the list is already fresh.</summary>
    public void OnOpened() => _ = LoadAsync(force: false);

    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------

    private async Task LoadAsync(bool force, bool more = false)
    {
        if (Busy) return;

        var controller = RelayTheSpireMod.Controller;
        if (controller is null) { Rebuild(); return; }

        Busy = true;
        _status.Text      = "Loading rooms...";
        _status.Modulate  = MutedColor;
        _refresh.Disabled = true;
        _more.Disabled    = true;

        try
        {
            if (more) await controller.Browser.LoadMoreAsync(RelayLocalStore.IsHidden);
            else      await controller.Browser.OpenAsync(RelayLocalStore.IsHidden, force);
            LastNote = null;
        }
        catch (Exception ex)
        {
            LastNote = $"Couldn't load rooms: {Shorten(ex.Message)}";
            Log.Warn($"[RelayTheSpire] Room browser load failed: {ex}");
        }
        finally
        {
            Busy = false;
        }

        if (GodotObject.IsInstanceValid(this) && IsInsideTree())
            Rebuild();
    }

    private async Task LoadDetailsAsync(string roomId)
    {
        if (Busy) return;

        var controller = RelayTheSpireMod.Controller;
        if (controller is null) return;

        Busy = true;
        _status.Text      = "Loading room details...";
        _status.Modulate  = MutedColor;
        _refresh.Disabled = true;
        _more.Disabled    = true;

        try
        {
            await controller.Browser.GetDetailsAsync(roomId, RelayTheSpireMod.GetLocalPlayerHash());
            LastNote = null;
        }
        catch (Exception ex)
        {
            LastNote = $"Couldn't load details: {Shorten(ex.Message)}";
            Log.Warn($"[RelayTheSpire] Room details failed: {ex}");
        }
        finally
        {
            Busy = false;
        }

        if (GodotObject.IsInstanceValid(this) && IsInsideTree())
            Rebuild();
    }

    private void OnUnhidePressed()
    {
        RelayLocalStore.UnhideAll();
        // Hidden rooms were skipped while paging, so start again from the marker list.
        RelayTheSpireMod.Controller?.Browser.Invalidate();
        _ = LoadAsync(force: false);
    }

    // -------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------

    private void Rebuild()
    {
        foreach (var child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        var controller = RelayTheSpireMod.Controller;
        if (controller is null)
        {
            _status.Text      = "RelayTheSpire.cfg not configured. Check your mod folder.";
            _status.Modulate  = WarnColor;
            _refresh.Disabled = true;
            _more.Visible     = false;
            return;
        }

        var snap   = controller.Browser.Snapshot();
        var hidden = RelayLocalStore.HiddenCount();
        var now    = RelayManifest.NowUnix();
        var shown  = snap.Rooms.Where(r => !RelayLocalStore.IsHidden(r.Room.RoomId)).ToList();

        if (snap.Community is { } community)
            _rows.AddChild(BuildRow(community, now));
        foreach (var entry in shown)
            _rows.AddChild(BuildRow(entry, now));

        if (snap.Loaded && shown.Count == 0)
        {
            var empty = new Label
            {
                Text              = "No other public rooms are active right now. Create one with \"List this room publicly\", " +
                                    "or share a room ID with friends.",
                AutowrapMode      = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(340, 0),
                Modulate          = MutedColor,
            };
            empty.AddThemeFontSizeOverride("font_size", 13);
            _rows.AddChild(empty);
        }

        var text = !snap.Loaded
            ? "Open this tab to browse public rooms."
            : $"{shown.Count} public room{(shown.Count == 1 ? "" : "s")}, plus Community.";
        if (snap.Failed > 0)    text += $" {snap.Failed} couldn't be read.";
        if (LastNote is { } n)  text += " " + n;

        _status.Text      = text;
        _status.Modulate  = LastNote is null ? MutedColor : WarnColor;
        _refresh.Disabled = Busy;
        _more.Visible     = snap.HasMore;
        _more.Disabled    = Busy;
        _unhide.Visible   = hidden > 0;
        _unhide.Text      = $"Unhide all ({hidden})";
    }

    private Control BuildRow(RoomBrowseEntry entry, long now)
    {
        var room = entry.Room;

        var row = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = RelayStartContext.CodeHidden ? "" : $"Room ID: {room.RoomId}",
        };
        row.AddThemeConstantOverride("separation", 2);

        // Title line + buttons.
        var top = new HBoxContainer();
        row.AddChild(top);

        var title = new Label
        {
            Text                = RelayStartContext.NameOf(room),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText            = true,
        };
        title.AddThemeFontSizeOverride("font_size", 15);
        top.AddChild(title);

        var details = new Button { Text = "Details", TooltipText = "Count this room's ongoing and finished runs." };
        details.Pressed += () => _ = LoadDetailsAsync(room.RoomId);
        top.AddChild(details);

        var join = new Button { Text = "Join", TooltipText = "Put this room in the Room ID field and go to Play." };
        join.Pressed += () => JoinRequested?.Invoke(room);
        top.AddChild(join);

        if (!entry.IsCommunity)
        {
            var hide = new Button { Text = "Hide", TooltipText = "Don't show this room again (only on this computer)." };
            hide.Pressed += () =>
            {
                RelayLocalStore.Hide(room.RoomId);
                Rebuild();
            };
            top.AddChild(hide);
        }

        // Rules in plain words.
        var rules = new Label
        {
            Text              = room.DescribeRules(),
            AutowrapMode      = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(340, 0),
        };
        rules.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(rules);

        // Last activity.
        var activity = new Label
        {
            Text     = entry.LastActivity == 0
                ? "Not started yet. The first player to take the baton creates it."
                : $"Last active {FormatAgo(now - entry.LastActivity)}",
            Modulate = MutedColor,
        };
        activity.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(activity);

        // Live counts, only after Details (and only while the cached result is fresh).
        if (RelayTheSpireMod.Controller!.Browser.TryGetCachedDetails(room.RoomId, out var stats))
        {
            var counts = new Label
            {
                Text     = $"{stats.Ongoing} ongoing · {stats.Ended} finished · {stats.Available} you could take now",
                Modulate = stats.Available > 0 ? GoodColor : MutedColor,
            };
            counts.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(counts);
        }

        row.AddChild(new HSeparator());
        return row;
    }

    private static string FormatAgo(long seconds) =>
        seconds < 120    ? "just now"
        : seconds < 7200   ? $"{seconds / 60} min ago"
        : seconds < 172800 ? $"{seconds / 3600} h ago"
        : $"{seconds / 86400} days ago";

    private static string Shorten(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 90 ? line[..90] + "..." : line;
    }
}
