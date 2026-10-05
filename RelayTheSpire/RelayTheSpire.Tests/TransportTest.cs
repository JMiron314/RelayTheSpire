using System;
using System.IO;
using System.Threading.Tasks;
using RelayTheSpire;

/// <summary>
/// Standalone console test — run this with:
///   dotnet run --project RelayTheSpire.Tests
///
/// It will:
///   1. Parse a real current_run.save
///   2. Create a manifest from it
///   3. Push the bundle to GitHub
///   4. Pull it back and verify round-trip integrity
///   5. Check that the anti-replay guard blocks a second pull from the same player
///
/// Set the environment variables before running:
///   STS2_RELAY_OWNER   — your GitHub username
///   STS2_RELAY_REPO    — your test repo name (e.g. "sts2-relay-test")
///   STS2_RELAY_TOKEN   — your GitHub PAT
///   STS2_RELAY_SAVE    — path to a current_run.save to use as test input
/// </summary>
class TransportTest
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== STS2 Relay Mode — Transport Test ===\n");

        // -------------------------------------------------------
        // Load config from environment
        // -------------------------------------------------------
        var owner    = Env("STS2_RELAY_OWNER");
        var repo     = Env("STS2_RELAY_REPO");
        var token    = Env("STS2_RELAY_TOKEN");
        var savePath = Env("STS2_RELAY_SAVE");

        if (owner is null || repo is null || token is null || savePath is null)
        {
            Console.Error.WriteLine(
                "Missing required environment variables.\n" +
                "Set: STS2_RELAY_OWNER, STS2_RELAY_REPO, STS2_RELAY_TOKEN, STS2_RELAY_SAVE");
            Environment.Exit(1);
        }

        // -------------------------------------------------------
        // Step 1: Parse the save file
        // -------------------------------------------------------
        Console.WriteLine($"[1] Parsing save file: {savePath}");
        var rawSave = await File.ReadAllTextAsync(savePath);
        var save    = ActiveSaveData.Parse(rawSave);

        if (save is null)
        {
            Fail("Could not parse the save file. Is it a valid current_run.save?");
            return;
        }

        Console.WriteLine($"    Run ID (seed): {save.Seed}");
        Console.WriteLine($"    Character:     {save.CharacterId}");
        Console.WriteLine($"    Ascension:     {save.Ascension}");
        Console.WriteLine($"    Floors done:   {save.VisitedMapCoordsCount}");
        Console.WriteLine($"    Pre-finished:  {save.IsPreFinished}");
        Console.WriteLine($"    Description:   {save.Describe()}");
        Pass("Save file parsed successfully.");

        // -------------------------------------------------------
        // Step 2: Create a manifest
        // -------------------------------------------------------
        Console.WriteLine("\n[2] Creating relay manifest...");
        var manifest = RelayManifest.CreateNew(save);
        var player1Hash = RelayManifest.HashPlayerId("test-player-1-steam-id");
        manifest.RecordTurn(player1Hash, save);

        Console.WriteLine($"    Run ID:     {manifest.RunId}");
        Console.WriteLine($"    Chain size: {manifest.RelayChain.Count}");
        Console.WriteLine($"    Player 1 hash (first 16): {player1Hash[..16]}...");
        Pass("Manifest created.");

        // -------------------------------------------------------
        // Step 3: Push to GitHub
        // -------------------------------------------------------
        Console.WriteLine($"\n[3] Pushing bundle to GitHub ({owner}/{repo})...");
        var transport = new GitHubTransport(new GitHubTransport.Config(owner, repo, token));
        var bundle    = new RelayBundle(rawSave, manifest);

        try
        {
            await transport.PushAsync(save.Seed, bundle);
            Pass($"Bundle pushed. Run path: runs/{save.Seed}/");
        }
        catch (RelayTransportException ex)
        {
            Fail($"Push failed: {ex.Message}");
            return;
        }

        // -------------------------------------------------------
        // Step 4: Pull back and verify round-trip
        // -------------------------------------------------------
        Console.WriteLine("\n[4] Pulling bundle back from GitHub...");
        RelayBundle? pulled;
        try
        {
            pulled = await transport.PullAsync(save.Seed);
        }
        catch (RelayTransportException ex)
        {
            Fail($"Pull failed: {ex.Message}");
            return;
        }

        if (pulled is null)
        {
            Fail("Pull returned null — file not found on GitHub after push.");
            return;
        }

        // Verify the save round-tripped cleanly
        if (pulled.SaveFileContent != rawSave)
        {
            Fail("Save file content changed during round-trip! Byte-for-byte comparison failed.");
            Console.Error.WriteLine($"  Original length: {rawSave.Length}");
            Console.Error.WriteLine($"  Pulled length:   {pulled.SaveFileContent.Length}");
            return;
        }
        Pass("Save file round-tripped byte-for-byte.");

        // Verify manifest
        if (pulled.Manifest.RunId != manifest.RunId)
        {
            Fail($"Manifest RunId mismatch: expected '{manifest.RunId}', got '{pulled.Manifest.RunId}'");
            return;
        }
        if (pulled.Manifest.RelayChain.Count != 1)
        {
            Fail($"Expected 1 relay chain entry, got {pulled.Manifest.RelayChain.Count}");
            return;
        }
        Pass("Manifest round-tripped correctly.");

        // -------------------------------------------------------
        // Step 5: Anti-replay check
        // -------------------------------------------------------
        Console.WriteLine("\n[5] Testing anti-replay guard...");

        // Player 1 tries to pull again — should be blocked
        if (!pulled.Manifest.HasPlayerPlayed(player1Hash))
        {
            Fail("Anti-replay FAILED: Player 1 should have been blocked but wasn't.");
            return;
        }
        Pass("Player 1 correctly blocked from replaying.");

        // Player 2 should be allowed
        var player2Hash = RelayManifest.HashPlayerId("test-player-2-steam-id");
        if (pulled.Manifest.HasPlayerPlayed(player2Hash))
        {
            Fail("Anti-replay FAILED: Player 2 was incorrectly blocked.");
            return;
        }
        Pass("Player 2 correctly allowed through.");

        // -------------------------------------------------------
        // Done
        // -------------------------------------------------------
        Console.WriteLine("\n========================================");
        Console.WriteLine("All tests passed. Transport layer works.");
        Console.WriteLine($"View the run on GitHub: https://github.com/{owner}/{repo}/tree/main/runs/{save.Seed}");
    }

    // -------------------------------------------------------
    // Helpers
    // -------------------------------------------------------

    static string? Env(string key) => Environment.GetEnvironmentVariable(key);

    static void Pass(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"    ✓ {msg}");
        Console.ResetColor();
    }

    static void Fail(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"    ✗ {msg}");
        Console.ResetColor();
        Environment.Exit(1);
    }
}
