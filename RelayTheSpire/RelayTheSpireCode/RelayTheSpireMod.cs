using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Platform;
using System;
using System.IO;

namespace RelayTheSpire;

[ModInitializer(nameof(Initialize))]
public static class RelayTheSpireMod
{
    // -------------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------------

    public static RelayController? Controller { get; private set; }

    /// <summary>
    /// One-shot message for the main menu panel to show on its next _Ready. Used after a
    /// forced exit to the menu, since the in-run toast is destroyed along with the run scene.
    /// </summary>
    public static string? PendingMenuMessage { get; set; }

    /// <summary>
    /// Fired after a successful relay push — the turn has been passed.
    /// UI subscribes to this to show a "Turn passed!" confirmation.
    /// </summary>
    public static event Action<ActiveSaveData, RelayManifest>? TurnPassed;

    /// <summary>
    /// Fired when a relay push fails.
    /// UI subscribes to this to show an error message.
    /// </summary>
    public static event Action<string>? PushFailed;

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    public static void Initialize()
    {
        Log.Info("[RelayTheSpire] Initializing...");
        try
        {
            new Harmony("com.relayTheSpire.mod").PatchAll(typeof(RelayTheSpireMod).Assembly);
            Log.Info("[RelayTheSpire] Harmony patches applied.");

            var config = LoadConfig();
            if (config != null)
            {
                RepeatPolicy.Current = config.RepeatPolicy;
                Log.Info($"[RelayTheSpire] Repeat policy: {config.RepeatPolicy.Describe()}");
                Log.Info($"[RelayTheSpire] Floors per turn: {config.FloorsPerTurn}");
                Controller = new RelayController(config);
                Log.Info($"[RelayTheSpire] Controller ready. Repo: {config.RepoOwner}/{config.RepoName}");
            }
            else
            {
                Log.Warn("[RelayTheSpire] No valid config found. GitHub sync disabled.");
            }

            Log.Info("[RelayTheSpire] Initialization complete.");
        }
        catch (Exception ex)
        {
            Log.Error($"[RelayTheSpire] Initialization failed: {ex}");
        }
    }

    // -------------------------------------------------------------------------
    // Player identity
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the SHA-256 hash of the local player's Steam ID.
    /// Used for anti-replay checks in the manifest.
    /// </summary>
    public static string GetLocalPlayerHash()
    {
        var steamId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
        return RelayManifest.HashPlayerId(steamId.ToString());
    }

    // -------------------------------------------------------------------------
    // Event dispatch
    // -------------------------------------------------------------------------

    public static void OnTurnPassed(ActiveSaveData save, RelayManifest manifest)
    {
        try { TurnPassed?.Invoke(save, manifest); }
        catch (Exception ex) { Log.Error($"[RelayTheSpire] Error in TurnPassed handler: {ex}"); }
    }

    public static void OnPushFailed(string error)
    {
        try { PushFailed?.Invoke(error); }
        catch (Exception ex) { Log.Error($"[RelayTheSpire] Error in PushFailed handler: {ex}"); }
    }

    // -------------------------------------------------------------------------
    // Config loading
    // -------------------------------------------------------------------------

    private static RelayConfig? LoadConfig()
    {
        var modDir     = Path.GetDirectoryName(typeof(RelayTheSpireMod).Assembly.Location) ?? ".";
        var configPath = Path.Combine(modDir, "RelayTheSpire.cfg");

        if (!File.Exists(configPath))
        {
            Log.Info($"[RelayTheSpire] No config file at: {configPath}");
            return null;
        }

        Log.Info($"[RelayTheSpire] Loading config from: {configPath}");
        var config = new RelayConfig();

        foreach (var rawLine in File.ReadAllLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#") || line.Length == 0 || !line.Contains('='))
                continue;

            var idx   = line.IndexOf('=');
            var key   = line[..idx].Trim().ToLowerInvariant();
            var value = line[(idx + 1)..].Trim();

            switch (key)
            {
                case "repeat_policy":
                    if (RepeatPolicy.TryParse(value, out var policy))
                        config.RepeatPolicy = policy;
                    else
                        Log.Warn($"[RelayTheSpire] Invalid repeat_policy '{value}' (use 'once' or a number >= 1). Using default.");
                    break;
                case "floors_per_turn":
                    if (int.TryParse(value, out var floorsPerTurn) && floorsPerTurn >= 1)
                        config.FloorsPerTurn = floorsPerTurn;
                    else
                        Log.Warn($"[RelayTheSpire] Invalid floors_per_turn '{value}' (use a number >= 1). Using default.");
                    break;
                case "claim_ttl_hours":
                    if (double.TryParse(value, out var hrs) && hrs > 0)
                        config.ClaimTtl = TimeSpan.FromHours(hrs);
                    break;
                case "github_owner":  config.RepoOwner   = value; break;
                case "github_repo":   config.RepoName    = value; break;
                case "github_token":  config.GitHubToken = value; break;
                case "github_branch": config.Branch      = value; break;
                case "exit_to_menu_after_push":
                    if (bool.TryParse(value, out var exitToMenu))
                        config.ExitToMenuAfterPush = exitToMenu;
                    break;
            }
        }

        if (string.IsNullOrEmpty(config.RepoOwner)  ||
            string.IsNullOrEmpty(config.RepoName)    ||
            string.IsNullOrEmpty(config.GitHubToken))
        {
            Log.Warn("[RelayTheSpire] Config missing required fields.");
            return null;
        }

        return config;
    }
}
