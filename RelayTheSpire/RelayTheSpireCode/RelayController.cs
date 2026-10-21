using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Logging;

namespace RelayTheSpire;

public static class RelaySavePaths
{
    private static FieldInfo? _saveStoreField;

    private static ISaveStore GetActiveSaveStore()
    {
        _saveStoreField ??= typeof(SaveManager).GetField("_saveStore", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_saveStoreField is null)
        {
            throw new InvalidOperationException(
                "[RelayTheSpire] Could not find SaveManager._saveStore via reflection. " +
                "The game was likely updated and its internal layout changed - " +
                "check SaveManager in ILSpy and update RelaySavePaths.GetActiveSaveStore.");
        }
        return (ISaveStore)_saveStoreField.GetValue(SaveManager.Instance)!;
    }

    public static string GetFilePath(string fileName)
    {
        var fragment  = RunSaveManager.GetRunSavePath(SaveManager.Instance.CurrentProfileId, fileName);
        var godotPath = GetActiveSaveStore().GetFullPath(fragment);
        return ProjectSettings.GlobalizePath(godotPath);
    }

    /// <summary>
    /// Writes a file through the game's own ISaveStore (CloudSaveStore when Steam is active).
    ///
    /// This MUST be used instead of File.WriteAllTextAsync for anything in the saves folder.
    /// CloudSaveStore.SyncCloudToLocal overwrites the local file with the Steam Cloud copy whenever
    /// the two last-modified timestamps differ at all, and deletes local files that don't exist in
    /// the cloud. A raw File write leaves the cloud copy stale with a different timestamp, so the
    /// next launch silently replaces the file we wrote. Writing through the store updates local and
    /// cloud together and syncs the timestamps (also giving us the game's tmp+fsync+rename write).
    /// </summary>
    public static Task WriteFileAsync(string fileName, string content)
    {
        var fragment = RunSaveManager.GetRunSavePath(SaveManager.Instance.CurrentProfileId, fileName);
        return GetActiveSaveStore().WriteFileAsync(fragment, content);
    }

    public static string GetSaveDirectory()
    {
        return Path.GetDirectoryName(GetFilePath("current_run.save"))!;
    }
    
    /// <summary>
    /// Deletes a file through the game's ISaveStore so Steam Cloud agrees (a raw File.Delete
    /// would be undone by the next cloud sync). No-op if the file doesn't exist.
    /// </summary>
    public static void DeleteFile(string fileName)
    {
        var fragment = RunSaveManager.GetRunSavePath(SaveManager.Instance.CurrentProfileId, fileName);
        var store    = GetActiveSaveStore();
        if (store.FileExists(fragment))
            store.DeleteFile(fragment);
    }
    
    /// <summary>
    /// Removes the player's local copy of a relay run after they've passed the baton, so
    /// "Continue" no longer offers it. The game's own DeleteCurrentRun removes current_run.save
    /// and current_run.save.backup through the ISaveStore (so Steam Cloud agrees); we then remove
    /// our manifest, which the game doesn't know about.
    /// </summary>
    public static void RemoveLocalRun()
    {
        SaveManager.Instance.DeleteCurrentRun();
        DeleteFile(RelayManifest.FileName);

        foreach (var name in new[] { "current_run.save", "current_run.save.backup", RelayManifest.FileName })
        {
            var real = GetFilePath(name);
            if (!File.Exists(real)) continue;

            Log.Warn($"[RelayTheSpire] {name} still on disk after store delete - deleting directly. " +
                     Diagnose(name));
            try { File.Delete(real); }
            catch (Exception ex) { Log.Error($"[RelayTheSpire] Direct delete of {name} failed: {ex}"); }
        }

        Log.Info($"[RelayTheSpire] Local run removed. HasRunSave={SaveManager.Instance.HasRunSave}");
    }

    /// <summary>
    /// One-line description of how the game and the filesystem each see a save file.
    /// Used when a file we expect is missing, to tell "wrong path" apart from "not written yet".
    /// </summary>
    public static string Diagnose(string fileName)
    {
        try
        {
            var fragment  = RunSaveManager.GetRunSavePath(SaveManager.Instance.CurrentProfileId, fileName);
            var store     = GetActiveSaveStore();
            var godotPath = store.GetFullPath(fragment);
            var real      = ProjectSettings.GlobalizePath(godotPath);
            var dir       = Path.GetDirectoryName(real);
            var listing   = dir != null && Directory.Exists(dir)
                ? string.Join(", ", Directory.GetFiles(dir).Select(f => Path.GetFileName(f)))
                : "(directory missing)";

            return $"fragment={fragment} | godotPath={godotPath} | real={real} | " +
                   $"File.Exists={File.Exists(real)} | store.FileExists={store.FileExists(fragment)} | " +
                   $"HasRunSave={SaveManager.Instance.HasRunSave} | dir=[{listing}]";
        }
        catch (Exception ex)
        {
            return $"Diagnose failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}

public class RelayController
{
    private readonly GitHubTransport _transport;
    private readonly RelayConfig     _config;
    /// <summary>Public room browsing (the Rooms tab). Holds its own caches for the session.</summary>
    public RoomBrowser Browser { get; }

    public bool ExitToMenuAfterPush => _config.ExitToMenuAfterPush;

    public RelayController(RelayConfig config)
    {
        _config    = config;
        _transport = new GitHubTransport(new GitHubTransport.Config(
            Owner:  config.RepoOwner,
            Repo:   config.RepoName,
            Token:  config.GitHubToken,
            Branch: config.Branch
        ));
        Browser = new RoomBrowser(_transport);   // <- new line
    }

    // -------------------------------------------------------------------------
    // Rooms
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a room. The result carries the ID the room actually got, which can differ from the one in
    /// <paramref name="desired"/> if that ID was taken, so show the returned one.
    /// </summary>
    public async Task<RoomResult> CreateRoomAsync(RoomSettings desired)
    {
        try
        {
            var room = await _transport.CreateRoomAsync(desired);

            // Listing is a second write. room.json comes first, so if the marker fails the room still
            // exists and works by ID; it just isn't browsable. Tell the creator instead of failing.
            string? warning = null;
            if (room.Listed)
            {
                try { await _transport.TryCreateListedMarkerAsync(room); }
                catch (RelayTransportException ex)
                {
                    warning = "The room was created, but it couldn't be listed publicly " +
                              $"({ex.Message.Split('\n')[0]}). Friends can still join with the Room ID.";
                }
            }

            return RoomResult.Ok(room, warning);
        }
        catch (ArgumentException ex)          { return RoomResult.Fail(ex.Message); }
        catch (RelayTransportException ex)    { return RoomResult.Fail($"Could not create the room: {ex.Message}"); }
    }
    
    public class RoomResult
    {
        public bool          Success { get; private set; }
        public string?       Error   { get; private set; }
        public RoomSettings? Room    { get; private set; }

        /// <summary>Something that didn't stop the room being created (e.g. it couldn't be listed).</summary>
        public string?       Warning { get; private set; }

        public static RoomResult Ok(RoomSettings room, string? warning = null) =>
            new() { Success = true, Room = room, Warning = warning };
        public static RoomResult Fail(string error) => new() { Error = error };
    }

    /// <summary>Looks up a room by an ID typed or pasted by the player.</summary>
    public async Task<RoomResult> FindRoomAsync(string rawRoomId)
    {
        if (!RelayScope.TryNormalizeRoomId(rawRoomId, out var id))
            return RoomResult.Fail("That doesn't look like a room ID.");

        try
        {
            var room = await _transport.GetRoomAsync(id);
            return room is null ? RoomResult.Fail($"No room with ID '{id}'.") : RoomResult.Ok(room);
        }
        catch (RelayTransportException ex)
        {
            return RoomResult.Fail($"Could not reach GitHub: {ex.Message}");
        }
    }

    /// <summary>A blank ID means the Community room; anything else is looked up as a room ID.</summary>
    public async Task<RoomResult> ResolveRoomAsync(string? rawRoomId)
    {
        if (!string.IsNullOrWhiteSpace(rawRoomId))
            return await FindRoomAsync(rawRoomId);

        try { return RoomResult.Ok(await EnsureCommunityRoomAsync()); }
        catch (RelayTransportException ex) { return RoomResult.Fail($"Could not reach GitHub: {ex.Message}"); }
    }

    /// <summary>
    /// Returns the Community room, creating it from the well-known defaults if nobody has yet. If two players
    /// create it at once, the create-only write lets exactly one win and both then read the winner's settings.
    /// </summary>
    private async Task<RoomSettings> EnsureCommunityRoomAsync()
    {
        var room = await _transport.GetRoomAsync(RelayScope.CommunityRoomId);
        if (room is not null) return room;

        await _transport.TryCreateRoomAsync(RoomSettings.CreateCommunity());

        return await _transport.GetRoomAsync(RelayScope.CommunityRoomId)
               ?? throw new RelayTransportException("The Community room could not be created.");
    }

    // -------------------------------------------------------------------------
    // Taking the baton
    // -------------------------------------------------------------------------

    /// <summary>What one room looks like to one player right now.</summary>
    internal sealed record RoomLookView(RoomSettings Room, RunListing? Best, RoomStats Stats);

    private async Task<RoomLookView> LookAtRoomAsync(RoomSettings room, string playerHash, long now)
    {
        var runs  = await _transport.ListRunsAsync(RelayScope.ForRoom(room.RoomId));
        var stats = RoomStats.Compute(runs, playerHash, now);
        return new RoomLookView(room, room.SelectRun(runs, playerHash, now), stats);
    }

    /// <summary>
    /// Decides what Take the Baton should do. With a room ID it looks at that room; blank means the Community
    /// room. The outcome is one of: take a run, start one automatically (the room is below its target), or
    /// offer to start one (the room is at or above its target).
    /// </summary>
    public async Task<BatonSearchResult> FindBatonAsync(string playerHash, string? rawRoomId = null)
    {
        try
        {
            var resolved = await ResolveRoomAsync(rawRoomId);
            if (!resolved.Success) return BatonSearchResult.Fail(resolved.Error!);

            return BatonSearchResult.From(
                await LookAtRoomAsync(resolved.Room!, playerHash, RelayManifest.NowUnix()));
        }
        catch (RelayTransportException ex)
        {
            return BatonSearchResult.Fail($"Could not reach GitHub: {ex.Message}");
        }
    }

    /// <summary>
    /// Looks up the current state of the given runs, one request each (up to 6 at a time). A run that can't be
    /// checked (network, rate limit, a corrupt manifest) is left out of <see cref="RunStatesResult.States"/> and
    /// counted in Failed, so the caller can keep showing what it knew before.
    /// </summary>
    public async Task<RunStatesResult> FetchRunStatesAsync(
        IReadOnlyList<(string RoomId, string RunId)> runs, string playerHash)
    {
        var now = RelayManifest.NowUnix();
        using var gate = new System.Threading.SemaphoreSlim(6);

        var tasks = runs.Select(async Task<(string RoomId, string RunId, RunLiveState? State, string? Error)> (r) =>
        {
            await gate.WaitAsync();
            try
            {
                var snap = await _transport.GetManifestWithShaAsync(RelayScope.ForRoom(r.RoomId), r.RunId);
                var state = snap is null
                    ? RunLiveState.Missing(r.RoomId, r.RunId)
                    : RunLiveState.From(r.RoomId, r.RunId, snap.Manifest, playerHash, now);
                return (r.RoomId, r.RunId, state, null);
            }
            catch (Exception ex) when (ex is RelayTransportException or ArgumentException)
            {
                return (r.RoomId, r.RunId, null, ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        var states = new Dictionary<(string RoomId, string RunId), RunLiveState>();
        foreach (var r in results)
            if (r.State is not null) states[(r.RoomId, r.RunId)] = r.State;

        return new RunStatesResult(states, results.Count(r => r.State is null),
                                   results.Select(r => r.Error).FirstOrDefault(e => e is not null));
    }

    public async Task<PullResult> PullRelayAsync(RoomSettings room, string runId, string playerHash)
    {
        var scope = RelayScope.ForRoom(room.RoomId);

        // ---- Claim first. The claim is the lock; the download comes after. ----
        ManifestSnapshot? snap;
        try { snap = await _transport.GetManifestWithShaAsync(scope, runId); }
        catch (RelayTransportException ex) { return PullResult.Fail($"Could not reach GitHub: {ex.Message}"); }

        if (snap is null)
            return PullResult.Fail($"No relay run found for ID '{runId}'.");

        var now = RelayManifest.NowUnix();
        switch (snap.Manifest.CheckClaim(playerHash, now))
        {
            case ClaimStatus.RunEnded:
                return PullResult.Fail($"This run has ended ({snap.Manifest.Status}) at floor " +
                                       $"{snap.Manifest.FloorsCompleted}. It can't be continued.");
            case ClaimStatus.AlreadyPlayed:
                return PullResult.Fail($"You can't take this turn yet.\n{snap.Manifest.Policy.Describe()}");
            case ClaimStatus.HeldByOther:
                var mins = snap.Manifest.ClaimSecondsRemaining(now) / 60;
                return PullResult.Fail($"Another player is currently playing this run " +
                                       $"(their claim expires in ~{mins} min).");
        }

        snap.Manifest.ApplyClaim(playerHash, now, room.ClaimTtl);
        ClaimAttempt claim;
        try { claim = await _transport.TryClaimAsync(scope, runId, snap.Manifest, snap.Sha); }
        catch (RelayTransportException ex) { return PullResult.Fail($"Could not claim the run: {ex.Message}"); }

        if (!claim.Claimed)
            return PullResult.Fail("Someone else claimed this run a moment ago. Try Take the Baton again.");

        // ---- We own the turn. Now download. ----
        RelayBundle? bundle;
        try { bundle = await _transport.PullAsync(scope, runId); }
        catch (RelayTransportException ex) { return PullResult.Fail($"Could not reach GitHub: {ex.Message}"); }

        if (bundle is null)
            return PullResult.Fail($"No relay run found for ID '{runId}'.");

        var save = ActiveSaveData.Parse(bundle.SaveFileContent);
        if (save is null)
            return PullResult.Fail("The downloaded save file could not be parsed. It may be corrupt.");

        var validationError = bundle.Manifest.ValidateAgainstSave(save);
        if (validationError is not null)
            return PullResult.Fail($"Manifest/save mismatch: {validationError}");

        try
        {
            var savePath     = RelaySavePaths.GetFilePath("current_run.save");
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);

            await RelaySavePaths.WriteFileAsync("current_run.save", bundle.SaveFileContent);
            await RelaySavePaths.WriteFileAsync(RelayManifest.FileName, bundle.Manifest.ToJson());
        }
        catch (Exception ex)
        {
            return PullResult.Fail($"Failed to write save files to disk: {ex.Message}");
        }

        return PullResult.Ok(save, bundle.Manifest);
    }

    // -------------------------------------------------------------------------
    // Starting a run
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers a brand-new run in a room, before the game has made its first save. Writes the floor-0
    /// manifest with a create-only PUT, already claimed by the creator, so the run exists in the room (and
    /// counts toward its target) from the moment it begins. If the write can't be made, the caller should
    /// block the run from starting rather than create one nobody can track.
    ///
    /// <see cref="BeginRunResult.SeedTaken"/> means a run folder with this seed already exists: pick another
    /// seed and call again. The returned manifest is exactly what was written remotely; save it locally as is.
    /// </summary>
    public async Task<BeginRunResult> BeginRunAsync(
        RoomSettings room, string playerHash, string seed, string characterId, int ascension)
    {
        var manifest = RelayManifest.CreateNew(seed, characterId, ascension);
        room.StampRules(manifest);
        manifest.ApplyClaim(playerHash, RelayManifest.NowUnix(), room.ClaimTtl);

        try
        {
            var attempt = await _transport.TryCreateManifestAsync(RelayScope.ForRoom(room.RoomId), seed, manifest);
            return attempt.Created ? BeginRunResult.Ok(manifest) : BeginRunResult.TakenSeed();
        }
        catch (RelayTransportException ex)
        {
            return BeginRunResult.Fail($"Could not register the run on GitHub: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Passing the baton
    // -------------------------------------------------------------------------

    /// <summary>The room a run belongs to, from its manifest. Null if it isn't part of one.</summary>
    private static RelayScope? ScopeOf(RelayManifest manifest) =>
        RelayScope.TryNormalizeRoomId(manifest.RoomId, out var id) ? RelayScope.ForRoom(id) : null;

    public async Task<PushResult> PushRelayAsync(string savePath, RelayManifest manifest)
    {
        var scope = ScopeOf(manifest);
        if (scope is null)
            return PushResult.Fail("This run isn't part of a relay room, so it can't be pushed.");

        string saveJson;
        try { saveJson = await File.ReadAllTextAsync(savePath); }
        catch (Exception ex) { return PushResult.Fail($"Could not read save file: {ex.Message}"); }

        try
        {
            var remote = await _transport.GetManifestWithShaAsync(scope, manifest.RunId);
            if (remote is not null)
            {
                var me  = manifest.RelayChain[^1].PlayerHash;
                var r   = remote.Manifest;
                var now = RelayManifest.NowUnix();

                if (r.IsEnded)
                    return PushResult.Fail("This run has already ended on the relay.");
                if (r.RelayChain.Count != manifest.RelayChain.Count - 1)
                    return PushResult.Fail("Another player already passed this turn. Your progress was not pushed.");
                if (r.IsClaimActive(now) && !string.Equals(r.ClaimedBy, me, StringComparison.OrdinalIgnoreCase))
                    return PushResult.Fail("Your claim expired and another player took the run. Your progress was not pushed.");
            }

            await _transport.PushAsync(scope, manifest.RunId, new RelayBundle(saveJson, manifest));
        }
        catch (RelayTransportException ex)
        {
            return PushResult.Fail($"GitHub push failed: {ex.Message}");
        }
        return PushResult.Ok(manifest);
    }

    public async Task<PushResult> PushRunEndedAsync(RelayManifest manifest, string? saveJson)
    {
        var scope = ScopeOf(manifest);
        if (scope is null)
            return PushResult.Fail("This run isn't part of a relay room, so it can't be pushed.");

        try
        {
            if (saveJson != null)
                await _transport.PushAsync(scope, manifest.RunId, new RelayBundle(saveJson, manifest));
            else
                await _transport.PushManifestAsync(scope, manifest.RunId, manifest);
        }
        catch (RelayTransportException ex)
        {
            return PushResult.Fail($"GitHub push failed: {ex.Message}");
        }
        return PushResult.Ok(manifest);
    }
}

/// <summary>Outcome of <see cref="RelayController.FetchRunStatesAsync"/>.</summary>
public sealed record RunStatesResult(
    Dictionary<(string RoomId, string RunId), RunLiveState> States,
    int     Failed,
    string? FirstError);

public class PushResult
{
    public bool            Success  { get; private set; }
    public string?         Error    { get; private set; }
    public RelayManifest?  Manifest { get; private set; }

    public static PushResult Ok(RelayManifest manifest) =>
        new() { Success = true, Manifest = manifest };

    public static PushResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>Outcome of <see cref="RelayController.BeginRunAsync"/>.</summary>
public class BeginRunResult
{
    public bool           Success  { get; private set; }

    /// <summary>A run with this seed already exists in the room. Pick another seed and retry.</summary>
    public bool           SeedTaken { get; private set; }

    public string?        Error    { get; private set; }
    public RelayManifest? Manifest { get; private set; }

    public static BeginRunResult Ok(RelayManifest manifest) => new() { Success = true, Manifest = manifest };
    public static BeginRunResult TakenSeed()                => new() { SeedTaken = true };
    public static BeginRunResult Fail(string error)         => new() { Error = error };
}

public class RoomResult
{
    public bool          Success { get; private set; }
    public string?       Error   { get; private set; }
    public RoomSettings? Room    { get; private set; }

    public static RoomResult Ok(RoomSettings room) => new() { Success = true, Room = room };
    public static RoomResult Fail(string error)    => new() { Error = error };
}

/// <summary>What Take the Baton should do next. See <see cref="RelayController.FindBatonAsync"/>.</summary>
public enum BatonOutcome
{
    /// <summary>A run is waiting for this player: pull it.</summary>
    TakeRun,

    /// <summary>Nothing to take and the room is below its run target: start a new run without asking.</summary>
    StartRun,

    /// <summary>Nothing to take and the room is at or above its target: tell the player and let them choose to start one.</summary>
    OfferStartRun,
}

public class BatonSearchResult
{
    public bool           Success { get; private set; }
    public string?        Error   { get; private set; }

    public BatonOutcome   Outcome { get; private set; }

    /// <summary>The room the outcome applies to (the Community room for quick play with nothing to take).</summary>
    public RoomSettings?  Room    { get; private set; }

    public RoomStats      Stats   { get; private set; }

    /// <summary>The run to take. Set only when <see cref="Outcome"/> is TakeRun.</summary>
    public string?        RunId    { get; private set; }
    public RelayManifest? Manifest { get; private set; }

    internal static BatonSearchResult From(RelayController.RoomLookView look)
    {
        var outcome = look.Best is not null ? BatonOutcome.TakeRun
                    : look.Room.ShouldAutoStart(look.Stats) ? BatonOutcome.StartRun
                    : BatonOutcome.OfferStartRun;

        return new BatonSearchResult
        {
            Success  = true,
            Outcome  = outcome,
            Room     = look.Room,
            Stats    = look.Stats,
            RunId    = look.Best?.RunId,
            Manifest = look.Best?.Manifest,
        };
    }

    public static BatonSearchResult Fail(string error) =>
        new() { Error = error };
}

public class PullResult
{
    public bool            Success  { get; private set; }
    public string?         Error    { get; private set; }
    public ActiveSaveData? Save     { get; private set; }
    public RelayManifest?  Manifest { get; private set; }

    public static PullResult Ok(ActiveSaveData save, RelayManifest manifest) =>
        new() { Success = true, Save = save, Manifest = manifest };

    public static PullResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Connection settings from RelayTheSpire.cfg. Game rules (floors per turn, repeat policy, run target) are
/// not here: they belong to each room and travel with its runs.
/// </summary>
public class RelayConfig
{
    public string RepoOwner   { get; set; } = "";
    public string RepoName    { get; set; } = "";
    public string GitHubToken { get; set; } = "";
    public string Branch      { get; set; } = "main";

    /// <summary>After a successful push, force the player back to the main menu.</summary>
    public bool ExitToMenuAfterPush { get; set; } = true;
}
