using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Saves;
using RelayTheSpire.UI;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace RelayTheSpire.Patches;

/// <summary>
/// Registers a new relay run BEFORE the game starts it.
///
/// Why OnEmbarkPressed and not BeginRun: by the time the lobby calls BeginRun it has set _isBeginningRun,
/// after which SetReady/ChangeCharacter are ignored, so a failed registration could not be undone and the
/// player would be stuck on the screen. OnEmbarkPressed runs before SetReady, so skipping it is free.
///
/// Flow when "Relay run" is ticked (singleplayer only):
///   1. Prefix skips the original and starts RegisterThenEmbarkAsync.
///   2. We pick a seed, write the floor-0 manifest to the room (create-only, retry on a seed collision),
///      and write the local relay_manifest.json next to where current_run.save will appear.
///   3. We set NGame.DebugSeedOverride to that seed and run the original OnEmbarkPressed. The lobby reads
///      the override synchronously inside SetReady, so the game starts the run with OUR seed.
///   4. Postfix clears the override.
/// On any failure the run does not start, an error is shown, and the screen is left as it was.
/// The first real save appears at the first EnterMapCoord; EnterMapCoordPatch then fills in the identity.
/// </summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), "OnEmbarkPressed")]
internal static class RelayEmbarkPatch
{
    private const int MaxSeedAttempts = 5;
    private const string TutorialFtueKey = "accept_tutorials_ftue";

    private static readonly MethodInfo? OriginalMethod =
        AccessTools.Method(typeof(NCharacterSelectScreen), "OnEmbarkPressed");

    private static bool    _registering;     // a registration is in flight (ignores double clicks)
    private static string? _registeredSeed;  // set once registration succeeded; consumed by the pass-through call
    private static bool    _overrideSet;     // we own NGame.DebugSeedOverride right now

    private static bool Prefix(NCharacterSelectScreen __instance)
    {
        // Second pass (called from RegisterThenEmbarkAsync): let the original run with our seed.
        if (_registeredSeed is { } seed)
        {
            NGame.Instance.DebugSeedOverride = seed;
            _overrideSet = true;
            return true;
        }

        if (_registering) return false;
        if (!RelayStartContext.Enabled) return true;
        if (__instance.Lobby?.NetService.Type != NetGameType.Singleplayer) return true;

        // The game shows a one-time tutorial prompt and calls us again afterwards. Let it, so we
        // don't register a run that may then not start.
        if (!SaveManager.Instance.SeenFtue(TutorialFtueKey)) return true;

        if (RelayTheSpireMod.Controller is null)
        {
            ShowError(__instance, "RelayTheSpire.cfg is not configured. Check your mod folder, or untick \"Relay run\".");
            return false;
        }

        if (SaveManager.Instance.HasRunSave)
        {
            ShowError(__instance, "You already have a run in progress. Continue or abandon it first.");
            return false;
        }

        _registering = true;
        _ = RegisterThenEmbarkAsync(__instance);
        return false;
    }

    private static void Postfix()
    {
        if (!_overrideSet) return;

        _overrideSet    = false;
        _registeredSeed = null;
        if (NGame.Instance is { } game)
            game.DebugSeedOverride = null;
    }

    private static async Task RegisterThenEmbarkAsync(NCharacterSelectScreen screen)
    {
        RelayBlackout.Show("Registering relay run...");
        try
        {
            var controller = RelayTheSpireMod.Controller!;
            var lobby      = screen.Lobby;
            var character  = lobby.LocalPlayer.character;
            var ascension  = lobby.Ascension;

            // Room: already resolved by Take the Baton, or look up the Room ID field (blank = Community).
            var room = RelayStartContext.ArmedRoom;
            if (room is null)
            {
                var resolved = await controller.ResolveRoomAsync(RelayStartContext.LastRoomId);
                if (!resolved.Success)
                {
                    Fail(screen, resolved.Error!);
                    return;
                }
                room = resolved.Room!;
            }

            var playerHash = RelayTheSpireMod.GetLocalPlayerHash();

            // Register in the room. A seed collision just means "pick another seed".
            // The character may still be "random" here: the real one is filled in from the first save.
            BeginRunResult? registered = null;
            string seed = "";
            for (int attempt = 0; attempt < MaxSeedAttempts; attempt++)
            {
                seed = SeedHelper.GetRandomSeed();
                var result = await controller.BeginRunAsync(room, playerHash, seed, character.Id.ToString(), ascension);

                if (result.Success) { registered = result; break; }
                if (!result.SeedTaken) { Fail(screen, result.Error ?? "Could not register the run."); return; }
            }

            if (registered is null)
            {
                Fail(screen, "Could not find a free seed. Try again.");
                return;
            }

            // Local manifest first: the game only creates current_run.save, and our EnterMapCoord patch
            // treats a run as a relay run only when this file sits next to it.
            try
            {
                await RelaySavePaths.WriteFileAsync(RelayManifest.FileName, registered.Manifest!.ToJson());
            }
            catch (Exception ex)
            {
                Fail(screen, $"The run was registered, but its local manifest could not be written: {ex.Message}");
                return;
            }

            RelayLocalStore.Touch(room.RoomId, room.Name); // a room you start a run in is a room you use

            // The player could have backed out while we were on the network.
            if (!GodotObject.IsInstanceValid(screen) || screen.Lobby is null || !screen.IsVisibleInTree())
            {
                Log.Warn($"[RelayTheSpire] Character select closed while registering run {seed}; not starting it.");
                return;
            }

            Log.Info($"[RelayTheSpire] Registered run {seed} in {room.RoomId}; starting it.");
            _registeredSeed = seed;
            OriginalMethod!.Invoke(screen, new object?[] { null });
        }
        catch (Exception ex)
        {
            Log.Error($"[RelayTheSpire] Relay embark failed: {ex}");
            Fail(screen, $"Unexpected error: {ex.Message}");
        }
        finally
        {
            // Normally the Postfix has already cleared these. This covers failure paths and an exception
            // thrown by the original, so the seed and the override can never linger into a later run.
            _registeredSeed = null;
            if (_overrideSet)
            {
                _overrideSet = false;
                if (NGame.Instance is { } game) game.DebugSeedOverride = null;
            }
            _registering = false;
            RelayBlackout.Hide();
        }
    }

    private static void Fail(NCharacterSelectScreen screen, string message)
    {
        Log.Warn($"[RelayTheSpire] Relay embark blocked: {message}");
        ShowError(screen, message);
    }

    private static void ShowError(NCharacterSelectScreen screen, string message)
    {
        if (!GodotObject.IsInstanceValid(screen)) return;

        var dialog = new AcceptDialog { Title = "RelayTheSpire", DialogText = message };
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled  += dialog.QueueFree;
        screen.AddChild(dialog);
        dialog.PopupCentered();
    }
}

/// <summary>Keeps the "Relay run" checkbox in step with the character-select screen's lifecycle.</summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuOpened))]
internal static class RelayCharacterSelectOpenedPatch
{
    private static void Postfix(NCharacterSelectScreen __instance) =>
        __instance.GetNodeOrNull<RelayRunToggle>(RelayRunToggle.NodeName)?.Refresh(__instance);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuClosed))]
internal static class RelayCharacterSelectClosedPatch
{
    // After a successful embark the screen closes too, which is fine: the run is already registered.
    private static void Postfix() => RelayStartContext.Clear();
}
