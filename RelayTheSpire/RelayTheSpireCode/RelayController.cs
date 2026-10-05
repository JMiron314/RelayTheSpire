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

    public bool ExitToMenuAfterPush => _config.ExitToMenuAfterPush;
    
    public int FloorsPerTurn => Math.Max(1, _config.FloorsPerTurn);

    public RelayController(RelayConfig config)
    {
        _config    = config;
        _transport = new GitHubTransport(new GitHubTransport.Config(
            Owner:  config.RepoOwner,
            Repo:   config.RepoName,
            Token:  config.GitHubToken,
            Branch: config.Branch
        ));
    }

    public async Task<PushResult> PushRelayAsync(string savePath, RelayManifest manifest)
    {
        string saveJson;
        try { saveJson = await File.ReadAllTextAsync(savePath); }
        catch (Exception ex) { return PushResult.Fail($"Could not read save file: {ex.Message}"); }

        try
        {
            var remote = await _transport.GetManifestWithShaAsync(manifest.RunId);
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

            await _transport.PushAsync(manifest.RunId, new RelayBundle(saveJson, manifest));
        }
        catch (RelayTransportException ex)
        {
            return PushResult.Fail($"GitHub push failed: {ex.Message}");
        }
        return PushResult.Ok(manifest);
    }
    
    public async Task<PushResult> PushRunEndedAsync(RelayManifest manifest, string? saveJson)
    {
        try
        {
            if (saveJson != null)
                await _transport.PushAsync(manifest.RunId, new RelayBundle(saveJson, manifest));
            else
                await _transport.PushManifestAsync(manifest.RunId, manifest);
        }
        catch (RelayTransportException ex)
        {
            return PushResult.Fail($"GitHub push failed: {ex.Message}");
        }
        return PushResult.Ok(manifest);
    }

    /// <summary>
    /// Turns the player's current in-progress run into a relay run by writing a fresh
    /// relay_manifest.json next to current_run.save. This is the ONLY place a manifest is
    /// created - normal (non-relay) runs never get one, so they are never pushed.
    ///
    /// Nothing is pushed here; the first push happens when the player picks their next
    /// map node (EnterMapCoordPatch), which also records them as turn 1 in the chain.
    /// </summary>
    public async Task<StartResult> StartRelayAsync()
    {
        string savePath, manifestPath;
        try
        {
            savePath     = RelaySavePaths.GetFilePath("current_run.save");
            manifestPath = RelaySavePaths.GetFilePath(RelayManifest.FileName);
        }
        catch (Exception ex)
        {
            return StartResult.Fail($"Could not locate the save folder: {ex.Message}");
        }

        if (!File.Exists(savePath))
            return StartResult.Fail(
                "No run in progress. Start a new run in the game, Save & Quit to the main menu, " +
                "then click Start Relay.");

        ActiveSaveData? save;
        try
        {
            save = ActiveSaveData.Parse(await File.ReadAllTextAsync(savePath));
        }
        catch (Exception ex)
        {
            return StartResult.Fail($"Could not read your current run: {ex.Message}");
        }

        if (save is null)
            return StartResult.Fail("Your current run save could not be parsed.");

        // Already a relay run? Nothing to do - just remind the player of the run ID.
        if (File.Exists(manifestPath))
        {
            try
            {
                var existing = RelayManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
                if (existing != null && existing.MatchesRun(save))
                    return StartResult.Ok(save, existing, alreadyStarted: true);
            }
            catch
            {
                // Unreadable manifest: treat as stale and replace it below.
            }
        }

        // Don't clobber someone else's relay that happens to use the same run ID.
        try
        {
            if (await _transport.RunExistsAsync(save.Seed))
                return StartResult.Fail(
                    $"A relay with run ID '{save.Seed}' already exists on GitHub. " +
                    "Use Receive Turn to join it instead.");
        }
        catch (RelayTransportException ex)
        {
            return StartResult.Fail($"Could not reach GitHub: {ex.Message}");
        }

        var manifest = RelayManifest.CreateNew(save);
        try
        {
            await RelaySavePaths.WriteFileAsync(RelayManifest.FileName, manifest.ToJson());
        }
        catch (Exception ex)
        {
            return StartResult.Fail($"Failed to write relay manifest: {ex.Message}");
        }

        return StartResult.Ok(save, manifest, alreadyStarted: false);
    }

    /// <summary>
    /// Finds the run this player should take next: among all runs on GitHub whose manifest
    /// does NOT contain this player, the one furthest along (highest floor). Ties go to the
    /// most recently updated run.
    /// </summary>
    public async Task<BatonSearchResult> FindBatonAsync(string playerHash)
    {
        List<RunListing> runs;
        try
        {
            runs = await _transport.ListRunsAsync();
        }
        catch (RelayTransportException ex)
        {
            return BatonSearchResult.Fail($"Could not reach GitHub: {ex.Message}");
        }

        var now = RelayManifest.NowUnix();
        var open = runs
            .Where(r => r.Manifest.CheckClaim(playerHash, now)
                is ClaimStatus.Available or ClaimStatus.HeldByYou)
            .OrderByDescending(r => r.Manifest.FloorsCompleted)
            .ThenByDescending(r => r.Manifest.LastUpdated)
            .ToList();

        var best = open.FirstOrDefault();
        return BatonSearchResult.Ok(
            totalRuns:    runs.Count,
            finishedRuns: runs.Count(r => r.Manifest.IsEnded),
            playedByYou:  runs.Count - open.Count,
            best:         best);
    }

    public async Task<PullResult> PullRelayAsync(string runId, string playerHash)
    {
        // ---- Claim first. The claim is the lock; the download comes after. ----
        ManifestSnapshot? snap;
        try { snap = await _transport.GetManifestWithShaAsync(runId); }
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
                return PullResult.Fail($"You can't take this turn yet.\n{RepeatPolicy.Current.Describe()}");
            case ClaimStatus.HeldByOther:
                var mins = snap.Manifest.ClaimSecondsRemaining(now) / 60;
                return PullResult.Fail($"Another player is currently playing this run " +
                                       $"(their claim expires in ~{mins} min).");
        }

        snap.Manifest.ApplyClaim(playerHash, now, _config.ClaimTtl);
        ClaimAttempt claim;
        try { claim = await _transport.TryClaimAsync(runId, snap.Manifest, snap.Sha); }
        catch (RelayTransportException ex) { return PullResult.Fail($"Could not claim the run: {ex.Message}"); }

        if (!claim.Claimed)
            return PullResult.Fail("Someone else claimed this run a moment ago. Try Take the Baton again.");

        // ---- We own the turn. Now download. ----
        RelayBundle? bundle;
        try { bundle = await _transport.PullAsync(runId); }
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
            var manifestPath = RelaySavePaths.GetFilePath(RelayManifest.FileName);
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
}

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

public class StartResult
{
    public bool            Success        { get; private set; }
    public bool            AlreadyStarted { get; private set; }
    public string?         Error          { get; private set; }
    public ActiveSaveData? Save           { get; private set; }
    public RelayManifest?  Manifest       { get; private set; }

    public static StartResult Ok(ActiveSaveData save, RelayManifest manifest, bool alreadyStarted) =>
        new() { Success = true, Save = save, Manifest = manifest, AlreadyStarted = alreadyStarted };

    public static StartResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public class BatonSearchResult
{
    public bool           Success     { get; private set; }
    public string?        Error       { get; private set; }
    public int TotalRuns    { get; private set; }
    public int FinishedRuns { get; private set; }
    public int PlayedByYou  { get; private set; }
    
    /// <summary>Runs that are still going (total minus ended).</summary>
    public int OngoingRuns => TotalRuns - FinishedRuns;

    /// <summary>The chosen run, or null if no run is available to this player.</summary>
    public string?        RunId       { get; private set; }
    public RelayManifest? Manifest    { get; private set; }

    public static BatonSearchResult Ok(int totalRuns, int finishedRuns, int playedByYou, RunListing? best) =>
        new()
        {
            Success      = true,
            TotalRuns    = totalRuns,
            FinishedRuns = finishedRuns,
            PlayedByYou  = playedByYou,
            RunId        = best?.RunId,
            Manifest     = best?.Manifest,
        };

    public static BatonSearchResult Fail(string error) =>
        new() { Success = false, Error = error };
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

public class RelayConfig
{
    public string RepoOwner   { get; set; } = "";
    public string RepoName    { get; set; } = "";
    public string GitHubToken { get; set; } = "";
    public string Branch      { get; set; } = "main";

    /// <summary>After a successful push, force the player back to the main menu.</summary>
    public bool ExitToMenuAfterPush { get; set; } = true;
    
    /// <summary>Who may take another turn in a run they already played. See RepeatPolicy.</summary>
    public RepeatPolicy RepeatPolicy { get; set; } = RepeatPolicy.Cooldown(2);
    
    /// <summary>How many floors a player plays before the baton is passed. Always >= 1.</summary>
    public int FloorsPerTurn { get; set; } = 1;
    
    /// <summary>How long a claim blocks other players before it expires.</summary>
    public TimeSpan ClaimTtl { get; set; } = TimeSpan.FromHours(12);
}
