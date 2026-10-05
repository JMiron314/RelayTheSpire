using System;
using System.Linq;
using System.Threading.Tasks;

namespace RelayTheSpire.Tests;

/// <summary>
/// Tiny assertion helper so the test console app needs no test framework.
/// </summary>
internal static class T
{
    public static int Passed;
    public static int Failed;

    public static void Check(bool condition, string name)
    {
        if (condition) { Passed++; Console.WriteLine($"  PASS  {name}"); }
        else           { Failed++; Console.WriteLine($"  FAIL  {name}"); }
    }

    public static void Section(string title) => Console.WriteLine($"\n== {title} ==");
}

/// <summary>
/// Step 1: pure claim-rule tests. No network, no game. Needs only RelayManifest.cs and
/// ActiveSaveData.cs compiled into the test project.
/// </summary>
public static class ClaimRulesTests
{
    private const string Alice = "aaaa1111";
    private const string Bob   = "bbbb2222";
    private const long   Now   = 1_000_000;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private static RelayManifest Fresh() => new()
    {
        RunId = "TESTRUN", StartTime = 42, SchemaVersion = 1, FloorsCompleted = 5,
    };

    private static ActiveSaveData SaveAtFloor(int floors)
    {
        var coords = string.Join(",", Enumerable.Range(0, floors).Select(i => $"{{\"col\":{i},\"row\":{i}}}"));
        var json = $"{{\"rng\":{{\"seed\":\"TESTRUN\"}},\"start_time\":42,\"schema_version\":1," +
                   $"\"visited_map_coords\":[{coords}]}}";
        return ActiveSaveData.Parse(json)!;
    }

    public static int RunAll()
    {
        T.Section("Claim rules");

        // --- Availability ---------------------------------------------------------
        var m = Fresh();
        T.Check(m.CheckClaim(Alice, Now) == ClaimStatus.Available, "unclaimed run is Available");
        T.Check(m.CanBeClaimedBy(Alice, Now), "unclaimed run can be claimed");
        T.Check(!m.IsClaimActive(Now), "unclaimed run has no active claim");

        // --- Active claim by someone else ----------------------------------------
        m = Fresh();
        m.ApplyClaim(Alice, Now, Ttl);
        T.Check(m.IsClaimActive(Now + 1), "claim is active shortly after being made");
        T.Check(m.CheckClaim(Bob, Now + 1) == ClaimStatus.HeldByOther, "other player sees HeldByOther");
        T.Check(!m.CanBeClaimedBy(Bob, Now + 1), "other player cannot claim an active claim");
        T.Check(m.ClaimSecondsRemaining(Now + 100) == (long)Ttl.TotalSeconds - 100, "seconds remaining counts down");

        // --- Own claim ------------------------------------------------------------
        T.Check(m.CheckClaim(Alice, Now + 1) == ClaimStatus.HeldByYou, "claimant sees HeldByYou");
        T.Check(m.CanBeClaimedBy(Alice, Now + 1), "claimant may re-pull their own claim");

        // --- Expiry ---------------------------------------------------------------
        long expiry = m.ClaimExpiresAt!.Value;
        T.Check(m.IsClaimActive(expiry - 1), "claim still active 1s before expiry");
        T.Check(!m.IsClaimActive(expiry), "claim is expired at exactly the expiry second");
        T.Check(m.CheckClaim(Bob, expiry + 1) == ClaimStatus.Available, "expired claim frees the run for others");
        T.Check(m.ClaimSecondsRemaining(expiry + 1) == 0, "no seconds remaining after expiry");
        T.Check(m.ClaimedBy == Alice, "expiry alone does not erase ClaimedBy");

        // --- Malformed claim never blocks forever --------------------------------
        m = Fresh();
        m.ClaimedBy = Alice; // no expiry recorded
        T.Check(!m.IsClaimActive(Now), "claim with no expiry is treated as expired");
        T.Check(m.CanBeClaimedBy(Bob, Now), "claim with no expiry doesn't block others");
        
        // --- Repeat policy ---------------------------------------------------------
        var once = RepeatPolicy.OncePerRun;
        var cd2  = RepeatPolicy.Cooldown(2);

        m = Fresh();
        m.RelayChain.Add(new RelayEntry { PlayerHash = Alice });
        T.Check(m.IsBlockedFromRepeat(Alice, once) && m.IsBlockedFromRepeat(Alice, cd2), "last player blocked under both policies");
        T.Check(!m.IsBlockedFromRepeat(Bob, once) && !m.IsBlockedFromRepeat(Bob, cd2), "newcomer never blocked");

        m.RelayChain.Add(new RelayEntry { PlayerHash = Bob });
        T.Check(m.IsBlockedFromRepeat(Alice, cd2), "cooldown 2: one other turn since, still blocked");
        m.RelayChain.Add(new RelayEntry { PlayerHash = "cccc3333" });
        T.Check(!m.IsBlockedFromRepeat(Alice, cd2), "cooldown 2: two other turns since, Alice may return");
        T.Check(m.IsBlockedFromRepeat(Alice, once), "once: Alice is still blocked");
        T.Check(m.IsBlockedFromRepeat(Bob, cd2) && m.IsBlockedFromRepeat("cccc3333", cd2), "cooldown 2: last two players stay blocked");

        T.Check(RepeatPolicy.Cooldown(0) == RepeatPolicy.Cooldown(1), "cooldown is clamped to at least 1");
        T.Check(RepeatPolicy.TryParse("once", out var p1) && p1.IsOncePerRun, "parses 'once'");
        T.Check(RepeatPolicy.TryParse(" 3 ", out var p2) && p2.CooldownTurns == 3, "parses a number");
        T.Check(!RepeatPolicy.TryParse("0", out _) && !RepeatPolicy.TryParse("banana", out _), "rejects 0 and junk");

        // CheckClaim honours the configured policy
        var saved = RepeatPolicy.Current;
        try
        {
            m = Fresh();
            m.RelayChain.Add(new RelayEntry { PlayerHash = Alice });
            m.RelayChain.Add(new RelayEntry { PlayerHash = Bob });
            m.RelayChain.Add(new RelayEntry { PlayerHash = "cccc3333" });

            RepeatPolicy.Current = cd2;
            T.Check(m.CheckClaim(Alice, Now) == ClaimStatus.Available, "CheckClaim: returning player Available under cooldown 2");
            RepeatPolicy.Current = once;
            T.Check(m.CheckClaim(Alice, Now) == ClaimStatus.AlreadyPlayed, "CheckClaim: same player AlreadyPlayed under once");
            m.ApplyClaim(Alice, Now, Ttl);
            T.Check(m.CheckClaim(Alice, Now) == ClaimStatus.AlreadyPlayed, "repeat block wins over own claim");
        }
        finally { RepeatPolicy.Current = saved; }

        m = Fresh();
        m.RelayChain.Add(new RelayEntry { PlayerHash = Alice });
        m.RelayChain.Add(new RelayEntry { PlayerHash = Bob });
        T.Check(m.HasTakenCurrentTurn(Bob) && !m.HasTakenCurrentTurn(Alice), "current-turn check looks at last entry only");

        // --- Hash comparison is case-insensitive (matches HasPlayerPlayed) --------
        m = Fresh();
        m.ApplyClaim("ABCDEF", Now, Ttl);
        T.Check(m.CheckClaim("abcdef", Now) == ClaimStatus.HeldByYou, "claimant hash compare ignores case");

        // --- ApplyClaim fields ----------------------------------------------------
        m = Fresh();
        m.ApplyClaim(Alice, Now, TimeSpan.FromHours(2));
        T.Check(m.ClaimedBy == Alice && m.ClaimedAt == Now && m.ClaimExpiresAt == Now + 7200,
                "ApplyClaim stamps by/at/expires");
        m.ApplyClaim(Bob, Now + 10, Ttl); // takeover of an expired/own claim overwrites
        T.Check(m.ClaimedBy == Bob && m.ClaimedAt == Now + 10, "ApplyClaim overwrites a previous claim");

        // --- ClearClaim and RecordTurn -------------------------------------------
        m = Fresh();
        m.ApplyClaim(Alice, Now, Ttl);
        m.ClearClaim();
        T.Check(m.ClaimedBy is null && m.ClaimedAt is null && m.ClaimExpiresAt is null, "ClearClaim wipes all three fields");

        m = Fresh();
        m.ApplyClaim(Alice, Now, Ttl);
        m.RecordTurn(Alice, SaveAtFloor(8));
        T.Check(m.ClaimedBy is null && m.ClaimExpiresAt is null, "RecordTurn releases the claim");
        T.Check(m.FloorsCompleted == 8 && m.HasPlayerPlayed(Alice), "RecordTurn still updates floors and chain");
        T.Check(m.CheckClaim(Bob, Now) == ClaimStatus.Available, "run is available to the next player after a turn");

        // --- JSON compatibility ---------------------------------------------------
        m = Fresh();
        var plain = m.ToJson();
        T.Check(!plain.Contains("claimed_by") && !plain.Contains("claim_expires_at"),
                "unclaimed manifest omits claim keys from JSON");

        m.ApplyClaim(Alice, Now, Ttl);
        var round = RelayManifest.FromJson(m.ToJson())!;
        T.Check(round.ClaimedBy == Alice && round.ClaimedAt == Now && round.ClaimExpiresAt == m.ClaimExpiresAt,
                "claim fields survive a JSON round-trip");

        var legacy = RelayManifest.FromJson(
            "{\"relay_version\":1,\"run_id\":\"OLD\",\"start_time\":7,\"floors_completed\":3,\"relay_chain\":[]}");
        T.Check(legacy is not null && legacy.ClaimedBy is null && legacy.CheckClaim(Alice, Now) == ClaimStatus.Available,
                "manifest written before claims existed loads as unclaimed");
        
        m = Fresh();
        m.ApplyClaim(Alice, Now, Ttl);
        m.MarkEnded(Bob, RelayManifest.RunStatus.Lost, 9);
        T.Check(m.IsEnded && m.Status == "lost", "MarkEnded sets status");
        T.Check(m.FloorsCompleted == 9 && m.HasPlayerPlayed(Bob), "MarkEnded records final turn and floors");
        T.Check(m.ClaimedBy is null, "MarkEnded releases the claim");
        T.Check(m.CheckClaim(Alice, Now) == ClaimStatus.RunEnded, "ended run is RunEnded for everyone");
        T.Check(!m.CanBeClaimedBy(Alice, Now), "ended run can't be claimed");
        m.MarkEnded(Bob, RelayManifest.RunStatus.Lost, 9);
        T.Check(m.RelayChain.Count == 1, "MarkEnded doesn't double-add the same player");
        T.Check(RelayManifest.FromJson(m.ToJson())!.Status == "lost", "status survives JSON round-trip");
        T.Check(!Fresh().ToJson().Contains("\"status\""), "active manifest omits status");
        
        return T.Failed;
    }
}

/// <summary>
/// Step 2: integration tests against a real GitHub repo. Uses throwaway runs/CLAIMTEST-* folders.
///
/// There is no delete in the transport, so these folders stay in the repo afterwards;
/// use a scratch repo/branch, or delete them by hand. The token needs write access.
/// </summary>
public static class ClaimRaceTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private static string NewRunId() => $"CLAIMTEST-{Guid.NewGuid():N}"[..22];

    private static async Task SeedRunAsync(GitHubTransport admin, string runId)
    {
        var manifest = new RelayManifest
        {
            RunId = runId, StartTime = 1234, SchemaVersion = 1, Ascension = 0,
            CharacterId = "CHARACTER.TEST", GameMode = "standard", FloorsCompleted = 5,
            LastUpdated = RelayManifest.NowUnix(),
        };
        await admin.PushAsync(runId, new RelayBundle("{\"test\":true}", manifest));
    }

    public static async Task<int> RunAllAsync(GitHubTransport.Config cfg)
    {
        await SequentialStaleShaAsync(cfg);
        await ConcurrentRaceAsync(cfg, contenders: 4);
        await EligibilityAfterClaimAsync(cfg);
        return T.Failed;
    }

    /// <summary>
    /// Deterministic: both players read the manifest (same SHA), A claims, then B tries with the
    /// stale SHA. B must be rejected. This is the core guarantee everything else relies on.
    /// </summary>
    private static async Task SequentialStaleShaAsync(GitHubTransport.Config cfg)
    {
        T.Section("Stale SHA is rejected (deterministic)");
        var runId = NewRunId();
        using var admin = new GitHubTransport(cfg);
        using var a     = new GitHubTransport(cfg);
        using var b     = new GitHubTransport(cfg);
        await SeedRunAsync(admin, runId);

        var snapA = (await a.GetManifestWithShaAsync(runId))!;
        var snapB = (await b.GetManifestWithShaAsync(runId))!;
        T.Check(snapA.Sha == snapB.Sha, "both players read the same manifest SHA");

        var now = RelayManifest.NowUnix();
        snapA.Manifest.ApplyClaim("player-a", now, Ttl);
        var resA = await a.TryClaimAsync(runId, snapA.Manifest, snapA.Sha);
        T.Check(resA.Claimed, "first claimant wins");
        T.Check(resA.NewSha is not null && resA.NewSha != snapA.Sha, "winner receives the manifest's new SHA");

        snapB.Manifest.ApplyClaim("player-b", now, Ttl);
        var resB = await b.TryClaimAsync(runId, snapB.Manifest, snapB.Sha);
        T.Check(!resB.Claimed, "second claimant with a stale SHA is rejected");

        var remote = (await admin.GetManifestWithShaAsync(runId))!;
        T.Check(remote.Manifest.ClaimedBy == "player-a", "remote manifest still belongs to the first claimant");
        T.Check(remote.Sha == resA.NewSha, "winner's returned SHA matches the remote SHA");
    }

    /// <summary>
    /// All contenders read the same SHA first, then fire their claims simultaneously.
    /// Exactly one may win. (A loser may be rejected either for the stale file SHA or because the
    /// branch head moved under it; both correctly count as "lost the race".)
    /// </summary>
    private static async Task ConcurrentRaceAsync(GitHubTransport.Config cfg, int contenders)
    {
        T.Section($"Concurrent race, {contenders} contenders");
        var runId = NewRunId();
        using var admin = new GitHubTransport(cfg);
        await SeedRunAsync(admin, runId);

        var transports = Enumerable.Range(0, contenders).Select(_ => new GitHubTransport(cfg)).ToArray();
        try
        {
            var snaps = new ManifestSnapshot[contenders];
            for (int i = 0; i < contenders; i++)
                snaps[i] = (await transports[i].GetManifestWithShaAsync(runId))!;

            var now = RelayManifest.NowUnix();
            var attempts = await Task.WhenAll(Enumerable.Range(0, contenders).Select(async i =>
            {
                snaps[i].Manifest.ApplyClaim($"player-{i}", now, Ttl);
                return await transports[i].TryClaimAsync(runId, snaps[i].Manifest, snaps[i].Sha);
            }));

            int winners = attempts.Count(x => x.Claimed);
            T.Check(winners == 1, $"exactly one contender wins (winners: {winners})");

            int winnerIdx = Array.FindIndex(attempts, x => x.Claimed);
            var remote = (await admin.GetManifestWithShaAsync(runId))!;
            T.Check(winnerIdx >= 0 && remote.Manifest.ClaimedBy == $"player-{winnerIdx}",
                    "remote manifest records the winner");
        }
        finally
        {
            foreach (var t in transports) t.Dispose();
        }
    }

    /// <summary>After a claim lands, other players see it as active; the claimant sees it as theirs.</summary>
    private static async Task EligibilityAfterClaimAsync(GitHubTransport.Config cfg)
    {
        T.Section("Eligibility after a claim");
        var runId = NewRunId();
        using var admin = new GitHubTransport(cfg);
        using var a     = new GitHubTransport(cfg);
        await SeedRunAsync(admin, runId);

        var now  = RelayManifest.NowUnix();
        var snap = (await a.GetManifestWithShaAsync(runId))!;
        snap.Manifest.ApplyClaim("player-a", now, Ttl);
        var res = await a.TryClaimAsync(runId, snap.Manifest, snap.Sha);
        T.Check(res.Claimed, "claim succeeds on an unclaimed run");

        var remote = (await admin.GetManifestWithShaAsync(runId))!.Manifest;
        T.Check(remote.CheckClaim("player-b", now + 5) == ClaimStatus.HeldByOther, "other player sees the run as claimed");
        T.Check(remote.CheckClaim("player-a", now + 5) == ClaimStatus.HeldByYou, "claimant sees the run as theirs");
        T.Check(remote.CheckClaim("player-b", now + (long)Ttl.TotalSeconds + 1) == ClaimStatus.Available,
                "run frees up for others once the claim expires");

        // Claimant can refresh their own claim using the SHA returned from the first claim.
        remote.ApplyClaim("player-a", now + 60, Ttl);
        var refresh = await a.TryClaimAsync(runId, remote, res.NewSha!);
        T.Check(refresh.Claimed, "claimant can refresh using the SHA returned by the first claim");
    }
}
