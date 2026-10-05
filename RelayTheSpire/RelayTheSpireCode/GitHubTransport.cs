using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RelayTheSpire;

/// <summary>
/// Handles all communication with GitHub's Contents API.
///
/// Repository layout:
///   runs/{run_id}/current_run.save
///   runs/{run_id}/relay_manifest.json
///
/// Each relay run lives in its own folder keyed by the seed (run_id).
/// All files are stored as plaintext; GitHub shows them as readable JSON in the UI,
/// which makes it easy for community members to spectate runs in progress.
///
/// Claims: the manifest doubles as a lock. GitHub's Contents API rejects a PUT whose
/// `sha` doesn't match the file's current blob SHA, which gives us compare-and-swap.
/// Read the manifest + SHA (GetManifestWithShaAsync), decide the run is free, then write
/// the claim with THAT SHA (TryClaimAsync). If anyone wrote in between, the PUT is rejected.
///
/// A 409 is NOT proof that the manifest changed: GitHub also returns 409 when the branch head
/// moves because of an unrelated write (another run pushed to the same repo). TryClaimAsync
/// therefore re-reads the manifest after a 409 and only reports a lost race if the manifest's
/// SHA actually changed; otherwise it retries. See ClassifyConflict.
/// </summary>
public class GitHubTransport : IDisposable
{
    // -------------------------------------------------------------------------
    // Config
    // -------------------------------------------------------------------------

    public record Config(
        string Owner,       // GitHub username or org that owns the relay repo
        string Repo,        // Repository name, e.g. "sts2-relay"
        string Token,       // Personal access token with repo write scope
        string Branch = "main"
    );

    private const string ApiBase    = "https://api.github.com";
    private const string UserAgent  = "sts2-relay-mod/1.0";
    private const string RunsPrefix = "runs";

    /// <summary>
    /// How many times TryClaimAsync re-sends a claim after a 409 that turned out NOT to be a lost
    /// race (manifest SHA unchanged, i.e. the branch head moved for an unrelated reason).
    /// </summary>
    private const int MaxConflictRetries = 3;

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly Config      _config;
    private readonly HttpClient  _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public GitHubTransport(Config config)
    {
        _config = config;
        _http   = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.Token);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Downloads the relay bundle for a given run_id.
    /// Returns null if no relay exists for that run yet.
    /// Throws <see cref="RelayTransportException"/> on network or API errors.
    /// </summary>
    public async Task<RelayBundle?> PullAsync(string runId)
    {
        var saveContent     = await GetFileContentAsync(SavePath(runId));
        var manifestContent = await GetFileContentAsync(ManifestPath(runId));

        if (saveContent is null || manifestContent is null)
            return null;

        var manifest = RelayManifest.FromJson(manifestContent);
        if (manifest is null)
            throw new RelayTransportException($"relay_manifest.json for run '{runId}' is corrupt or unreadable.");

        return new RelayBundle(saveContent, manifest);
    }
    
    /// <summary>Writes only the manifest (used when the run ended and no save is worth pushing).</summary>
    public Task PushManifestAsync(string runId, RelayManifest manifest) =>
        PutFileAsync(ManifestPath(runId), manifest.ToJson(),
            $"Run {runId} ended ({manifest.Status}) at floor {manifest.FloorsCompleted} [manifest]");

    /// <summary>
    /// Uploads (creates or updates) the relay bundle for the given run_id.
    /// Call this after the player has finished their floor and the manifest has been updated.
    /// Throws <see cref="RelayTransportException"/> on network or API errors.
    /// </summary>
    public async Task PushAsync(string runId, RelayBundle bundle)
    {
        var saveJson     = bundle.SaveFileContent;
        var manifestJson = bundle.Manifest.ToJson();
        var message      = BuildCommitMessage(bundle.Manifest);

        // Push both files. Order matters: save first, then manifest.
        // If the save push fails we haven't touched the manifest, so the state stays consistent.
        await PutFileAsync(SavePath(runId),     saveJson,     $"{message} [save]");
        await PutFileAsync(ManifestPath(runId), manifestJson, $"{message} [manifest]");
    }

    /// <summary>
    /// Returns true if a relay run already exists in the repo for this run_id.
    /// Use this to decide whether to create a fresh manifest or pull an existing one.
    /// </summary>
    public async Task<bool> RunExistsAsync(string runId)
    {
        var content = await GetFileContentAsync(ManifestPath(runId));
        return content is not null;
    }

    /// <summary>
    /// Reads a run's manifest together with the blob SHA it had at read time.
    /// Returns null if the run doesn't exist. Hold on to the SHA: passing it back to
    /// <see cref="TryClaimAsync"/> is what makes the claim atomic.
    /// Throws <see cref="RelayTransportException"/> on network/API errors or a corrupt manifest.
    /// </summary>
    public async Task<ManifestSnapshot?> GetManifestWithShaAsync(string runId)
    {
        var file = await GetFileWithShaAsync(ManifestPath(runId));
        if (file is null)
            return null;

        var manifest = RelayManifest.FromJson(file.Value.Content)
                       ?? throw new RelayTransportException(
                           $"relay_manifest.json for run '{runId}' is corrupt or unreadable.");

        return new ManifestSnapshot(manifest, file.Value.Sha);
    }

    /// <summary>
    /// Attempts to write a claim by replacing the manifest, but ONLY if it is still exactly the
    /// version identified by <paramref name="expectedSha"/> (the SHA from
    /// <see cref="GetManifestWithShaAsync"/>). This is a manifest-only write; the save is untouched.
    ///
    /// Outcomes:
    ///   Claimed=true, NewSha=...   The claim landed. Keep NewSha if you need to write the manifest
    ///                              again later (e.g. at push time). Also returned if a 409 turns out to
    ///                              be our own earlier write (e.g. a timed-out request that did land).
    ///   Claimed=false, Current=... Someone else changed the manifest first: the caller lost the race.
    ///                              Current is the manifest as it now stands (for "expires in 5h" style
    ///                              messages); the caller should re-evaluate against it.
    ///   throws                     Auth, network, rate limit, the run vanishing, or GitHub still
    ///                              answering 409 after <see cref="MaxConflictRetries"/> retries even
    ///                              though the manifest never changed (a busy repo, not a lost race).
    ///
    /// A 409 alone never means "lost": GitHub also uses it when the branch head moves for an unrelated
    /// write. After a 409 we re-read the manifest and let <see cref="ClassifyConflict"/> decide.
    /// </summary>
    public async Task<ClaimAttempt> TryClaimAsync(string runId, RelayManifest claimedManifest, string expectedSha)
    {
        if (string.IsNullOrEmpty(expectedSha))
            throw new ArgumentException("expectedSha is required; without it the claim isn't atomic.", nameof(expectedSha));
        if (string.IsNullOrEmpty(claimedManifest.ClaimedBy))
            throw new ArgumentException("Manifest has no claim applied. Call ApplyClaim first.", nameof(claimedManifest));

        var who     = claimedManifest.ClaimedBy!;
        var message = $"Claim run {runId} at floor {claimedManifest.FloorsCompleted} " +
                      $"(player {who[..Math.Min(8, who.Length)]})";
        var json    = claimedManifest.ToJson();

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var newSha = await PutFileCoreAsync(
                    ManifestPath(runId), json, message, expectedSha, conditional: true);
                return new ClaimAttempt(true, newSha);
            }
            catch (ClaimConflictException ex)
            {
                // Don't trust the 409. Find out whether the manifest really changed.
                var remote = await GetManifestWithShaAsync(runId)
                             ?? throw new RelayTransportException(
                                 $"Run '{runId}' disappeared from GitHub while claiming it.", ex);

                switch (ClassifyConflict(remote, expectedSha, claimedManifest))
                {
                    case ConflictResolution.AlreadyOurs:
                        return new ClaimAttempt(true, remote.Sha);

                    case ConflictResolution.Lost:
                        return new ClaimAttempt(false, null, remote.Manifest);

                    case ConflictResolution.RetrySameSha:
                        if (attempt >= MaxConflictRetries)
                            throw new RelayTransportException(
                                $"GitHub kept rejecting the claim on '{runId}' even though its manifest " +
                                "didn't change (the repo may be busy). Try again in a moment.", ex);

                        // Short jittered backoff so simultaneous retriers don't collide in lockstep.
                        await Task.Delay(200 * (attempt + 1) + Random.Shared.Next(0, 150));
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Decides what a 409 on a conditional manifest write actually meant, given a fresh read of the
    /// manifest taken AFTER the rejection. Pure (no I/O) so it can be unit-tested.
    ///
    ///   RetrySameSha  The manifest's SHA is still the one we expected, so nobody changed it. The 409
    ///                 came from the branch head moving (an unrelated write). Safe to re-send as-is.
    ///   AlreadyOurs   The manifest changed, but it is our own claim (same claimant, same claimed_at):
    ///                 an earlier attempt of ours landed even though we never saw the response.
    ///   Lost          The manifest changed and it isn't our claim. Someone else wrote first.
    /// </summary>
    public static ConflictResolution ClassifyConflict(
        ManifestSnapshot remote, string expectedSha, RelayManifest attempted)
    {
        if (string.Equals(remote.Sha, expectedSha, StringComparison.Ordinal))
            return ConflictResolution.RetrySameSha;

        var r = remote.Manifest;
        bool sameClaim = !string.IsNullOrEmpty(r.ClaimedBy) &&
                         string.Equals(r.ClaimedBy, attempted.ClaimedBy, StringComparison.OrdinalIgnoreCase) &&
                         r.ClaimedAt == attempted.ClaimedAt;

        return sameClaim ? ConflictResolution.AlreadyOurs : ConflictResolution.Lost;
    }

    /// <summary>
    /// Lists every relay run in the repo along with its manifest. Used by "Take the Baton"
    /// to find a run that needs a player.
    ///
    /// Costs one request to list runs/ plus one per run to fetch its manifest (throttled).
    /// A failure on the listing itself (bad token, missing repo, rate limit) throws, so it isn't
    /// mistaken for "no runs"; a single unreadable run is skipped.
    /// Note: GitHub truncates directory listings at 1000 entries.
    /// </summary>
    public async Task<List<RunListing>> ListRunsAsync()
    {
        var runIds = await GetDirectoryNamesAsync(RunsPrefix);

        using var gate = new SemaphoreSlim(6);
        var tasks = runIds.Select(async Task<RunListing?> (runId) =>
        {
            await gate.WaitAsync();
            try
            {
                var json     = await GetFileContentAsync(ManifestPath(runId));
                var manifest = json is null ? null : RelayManifest.FromJson(json);
                return manifest is null ? null : new RunListing(runId, manifest);
            }
            catch (Exception)
            {
                return null; // Skip runs we can't read rather than failing the whole search.
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var all = await Task.WhenAll(tasks);
        return all.Where(r => r is not null).Select(r => r!).ToList();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>Names of the sub-directories of a repo path. Empty if the path doesn't exist.</summary>
    private async Task<List<string>> GetDirectoryNamesAsync(string path)
    {
        var response = await _http.GetAsync($"{ContentsUrl(path)}?ref={_config.Branch}");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new List<string>();

        await EnsureSuccessAsync(response, $"LIST {path}");

        var body    = await response.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<List<GitHubDirEntry>>(body, JsonOpts) ?? new();
        return entries.Where(e => e.Type == "dir" && e.Name is not null)
                      .Select(e => e.Name!)
                      .ToList();
    }

    private string SavePath(string runId)     => $"{RunsPrefix}/{runId}/current_run.save";
    private string ManifestPath(string runId) => $"{RunsPrefix}/{runId}/relay_manifest.json";

    private string ContentsUrl(string path) =>
        $"{ApiBase}/repos/{_config.Owner}/{_config.Repo}/contents/{path}";

    /// <summary>
    /// GETs a file from the repo. Returns its decoded text content, or null if 404.
    /// </summary>
    private async Task<string?> GetFileContentAsync(string path)
    {
        var file = await GetFileWithShaAsync(path);
        return file?.Content;
    }

    /// <summary>
    /// GETs a file from the repo. Returns its decoded text plus its blob SHA, or null if 404.
    /// </summary>
    private async Task<(string Content, string Sha)?> GetFileWithShaAsync(string path)
    {
        var url      = ContentsUrl(path);
        var response = await _http.GetAsync($"{url}?ref={_config.Branch}");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, $"GET {path}");

        var body     = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<GitHubContentResponse>(body, JsonOpts)
                       ?? throw new RelayTransportException($"Empty response from GitHub for path '{path}'.");

        // GitHub returns content with newlines inserted every 60 chars — strip them.
        var cleaned = envelope.Content?.Replace("\n", "").Replace("\r", "") ?? "";
        var text    = Encoding.UTF8.GetString(Convert.FromBase64String(cleaned));

        var sha = envelope.Sha
                  ?? throw new RelayTransportException($"GitHub returned no blob SHA for path '{path}'.");

        return (text, sha);
    }

    /// <summary>
    /// PUTs (creates or updates) a file. Fetches the current blob SHA first if the file exists,
    /// which GitHub requires for updates to prevent blind overwrites.
    ///
    /// NOTE: because the SHA is fetched right before the write, this can never detect a concurrent
    /// writer (last write wins). Use <see cref="TryClaimAsync"/> when you need real conflict detection.
    /// </summary>
    private async Task PutFileAsync(string path, string textContent, string commitMessage)
    {
        const int maxRetries = 3;
        for (int attempt = 0; ; attempt++)
        {
            var existingSha = await GetFileShaAsync(path);
            try
            {
                await PutFileCoreAsync(path, textContent, commitMessage, existingSha, conditional: false);
                return;
            }
            catch (ClaimConflictException) when (attempt < maxRetries)
            {
                // Stale SHA or a branch-head move. Back off briefly, then re-read the SHA and re-send.
                await Task.Delay(300 * (attempt + 1) + Random.Shared.Next(0, 150));
            }
        }
    }

    /// <summary>
    /// Sends the PUT. Returns the new blob SHA of the written file (from GitHub's response).
    /// When <paramref name="conditional"/> is true, <paramref name="sha"/> is the caller's expectation of the
    /// file's current version, and a 409 is thrown as <see cref="ClaimConflictException"/> instead of a
    /// generic transport error. A 409 is only a *candidate* lost race; the caller must confirm it
    /// (see TryClaimAsync) because GitHub also returns 409 when the branch head moves for other reasons.
    /// </summary>
    private async Task<string?> PutFileCoreAsync(
        string path, string textContent, string commitMessage, string? sha, bool conditional)
    {
        var url = ContentsUrl(path);

        var payload = new GitHubPutRequest
        {
            Message = commitMessage,
            Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(textContent)),
            Branch  = _config.Branch,
            Sha     = sha,   // null on first create; required for updates
        };

        var json     = JsonSerializer.Serialize(payload, JsonOpts);
        var request  = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PutAsync(url, request);
        var body     = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new ClaimConflictException(
                $"'{path}' was rejected with HTTP 409 (file changed, or the branch head moved).");

        // Anything else, including 422, is a real error and surfaces loudly. A 422 means the request
        // itself was malformed (e.g. a missing sha on an existing file); it must never be mistaken
        // for a lost race.
        await EnsureSuccessAsync(response, $"PUT {path}");

        try
        {
            return JsonSerializer.Deserialize<GitHubPutResponse>(body, JsonOpts)?.Content?.Sha;
        }
        catch (JsonException)
        {
            return null; // The write succeeded; we just couldn't read the new SHA back.
        }
    }

    /// <summary>
    /// Returns the blob SHA of an existing file, or null if it doesn't exist.
    /// Required by GitHub's API when updating (not creating) a file.
    /// </summary>
    private async Task<string?> GetFileShaAsync(string path)
    {
        var url      = ContentsUrl(path);
        var response = await _http.GetAsync($"{url}?ref={_config.Branch}");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, $"GET SHA {path}");

        var body     = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<GitHubContentResponse>(body, JsonOpts);
        return envelope?.Sha;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string context)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        throw new RelayTransportException(
            $"GitHub API error during '{context}': HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{body}");
    }

    private static string BuildCommitMessage(RelayManifest manifest)
    {
        var chain   = manifest.RelayChain;
        var turnNum = chain.Count;
        var floor   = manifest.FloorsCompleted;
        return $"Relay turn {turnNum} — floor {floor} ({manifest.CharacterId}, A{manifest.Ascension})";
    }

    public void Dispose() => _http.Dispose();

    // -------------------------------------------------------------------------
    // GitHub API DTOs
    // -------------------------------------------------------------------------

    private class GitHubContentResponse
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("sha")]     public string? Sha     { get; set; }
        [JsonPropertyName("name")]    public string? Name    { get; set; }
        [JsonPropertyName("path")]    public string? Path    { get; set; }
    }

    private class GitHubDirEntry
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private class GitHubPutRequest
    {
        [JsonPropertyName("message")] public string  Message { get; set; } = "";
        [JsonPropertyName("content")] public string  Content { get; set; } = "";
        [JsonPropertyName("branch")]  public string  Branch  { get; set; } = "main";
        [JsonPropertyName("sha")]     public string? Sha     { get; set; }  // null = create new file
    }

    // PUT response: { "content": { "sha": "<new blob sha>", ... }, "commit": { ... } }
    private class GitHubPutResponse
    {
        [JsonPropertyName("content")] public GitHubPutContent? Content { get; set; }
    }

    private class GitHubPutContent
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }
}

/// <summary>The in-memory representation of what travels between players.</summary>
public record RelayBundle(
    string         SaveFileContent,  // Raw text of current_run.save
    RelayManifest  Manifest
);

/// <summary>One run found in the repo: its folder name (the run ID) and its manifest.</summary>
public record RunListing(string RunId, RelayManifest Manifest);

/// <summary>A manifest plus the blob SHA it had when read. The SHA is the token for an atomic claim.</summary>
public record ManifestSnapshot(RelayManifest Manifest, string Sha);

/// <summary>
/// Outcome of <see cref="GitHubTransport.TryClaimAsync"/>.
/// Claimed=false means another writer got there first (not an error); Current is then the manifest
/// as it stands now, so callers can report who/when without another request.
/// NewSha is the manifest's SHA after our write (null when not claimed).
/// </summary>
public record ClaimAttempt(bool Claimed, string? NewSha, RelayManifest? Current = null);

/// <summary>What a 409 on a conditional manifest write actually meant. See GitHubTransport.ClassifyConflict.</summary>
public enum ConflictResolution
{
    /// <summary>Manifest unchanged; the 409 was an unrelated branch-head move. Re-send.</summary>
    RetrySameSha,

    /// <summary>Manifest changed, but it is our own claim (an earlier attempt landed).</summary>
    AlreadyOurs,

    /// <summary>Manifest changed and it isn't our claim: someone else wrote first.</summary>
    Lost,
}

/// <summary>Thrown when a GitHub API call fails for any reason.</summary>
public class RelayTransportException : Exception
{
    public RelayTransportException(string message) : base(message) { }
    public RelayTransportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A conditional write was rejected with a 409. This is only a CANDIDATE lost race: the branch head
/// can also move for unrelated reasons. TryClaimAsync catches this and confirms by re-reading the
/// manifest; it only escapes from lower-level helpers.
/// Derives from RelayTransportException so existing catch blocks still handle it.
/// </summary>
public class ClaimConflictException : RelayTransportException
{
    public ClaimConflictException(string message) : base(message) { }
}
