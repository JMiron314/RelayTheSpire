using System;

namespace RelayTheSpire.Tests;

/// <summary>
/// Pure tests for GitHubTransport.ClassifyConflict: what a 409 on a claim write actually meant.
/// No network. Add a call to ConflictClassificationTests.RunAll() next to ClaimRulesTests.RunAll()
/// in your test Program, and compile GitHubTransport.cs into the test project (it already is, for
/// ClaimRaceTests).
/// </summary>
public static class ConflictClassificationTests
{
    private const string Alice = "aaaa1111";
    private const string Bob   = "bbbb2222";
    private const long   Now   = 1_000_000;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private static RelayManifest Fresh() => new() { RunId = "TESTRUN", StartTime = 42, FloorsCompleted = 5 };

    public static int RunAll()
    {
        T.Section("409 classification");

        // The attempt we made: Alice claiming with expectedSha "sha-1".
        var attempt = Fresh();
        attempt.ApplyClaim(Alice, Now, Ttl);

        // Manifest SHA unchanged: the 409 was an unrelated branch-head move, so retry.
        var unchanged = new ManifestSnapshot(Fresh(), "sha-1");
        T.Check(GitHubTransport.ClassifyConflict(unchanged, "sha-1", attempt) == ConflictResolution.RetrySameSha,
                "unchanged SHA means retry, not a lost race");

        // Someone else claimed in between.
        var theirs = Fresh();
        theirs.ApplyClaim(Bob, Now + 1, Ttl);
        T.Check(GitHubTransport.ClassifyConflict(new ManifestSnapshot(theirs, "sha-2"), "sha-1", attempt)
                == ConflictResolution.Lost,
                "changed SHA with another player's claim is a lost race");

        // Manifest changed but carries no claim (e.g. a turn was pushed): our decision is stale, so lost.
        T.Check(GitHubTransport.ClassifyConflict(new ManifestSnapshot(Fresh(), "sha-2"), "sha-1", attempt)
                == ConflictResolution.Lost,
                "changed SHA with no claim is still a lost race (our read was stale)");

        // Same claimant but a different claimed_at: an older claim of ours, not this attempt.
        var older = Fresh();
        older.ApplyClaim(Alice, Now - 500, Ttl);
        T.Check(GitHubTransport.ClassifyConflict(new ManifestSnapshot(older, "sha-2"), "sha-1", attempt)
                == ConflictResolution.Lost,
                "same player but different claimed_at is not our attempt");

        // Our own write landed (lost response / timeout) and then we got a 409 on the re-send.
        var ours = Fresh();
        ours.ApplyClaim(Alice, Now, Ttl);
        T.Check(GitHubTransport.ClassifyConflict(new ManifestSnapshot(ours, "sha-2"), "sha-1", attempt)
                == ConflictResolution.AlreadyOurs,
                "remote holds exactly our claim, so treat as already ours");

        // Hash comparison ignores case, like the rest of the claim rules.
        var upper = Fresh();
        upper.ApplyClaim(Alice.ToUpperInvariant(), Now, Ttl);
        T.Check(GitHubTransport.ClassifyConflict(new ManifestSnapshot(upper, "sha-2"), "sha-1", attempt)
                == ConflictResolution.AlreadyOurs,
                "claimant comparison ignores case");

        return T.Failed;
    }
}
