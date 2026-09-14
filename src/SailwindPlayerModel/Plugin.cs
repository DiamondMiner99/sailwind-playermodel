using System;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Gives the player a body. Sailwind's local player is bodyless, so the ship-orbit camera looks down on an
    /// empty deck; this puts your own sailor there, animated from your movement.
    ///
    /// It is also the shared foundation other mods pose: the co-op mod builds every crewmate's body from the
    /// same rig and the same animation code, so a player's own body and the body their crew sees cannot
    /// disagree about how a squat looks. See <see cref="PlayerModel"/> for the API.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.diamondminer99.playermodel";
        public const string PluginName = "Sailwind Player Model";
        // BepInEx 5 parses this as a strict System.Version. No SemVer suffixes, or the plugin silently fails
        // to load with no error.
        public const string PluginVersion = "0.1.2";

        public static ManualLogSource Log;

        // Sailwind Co-op carried its own copy of the body and the pause menu until this version. Running
        // that alongside this mod puts two sailors on the deck and two parchment scrolls on Escape, because
        // both would clone a panel and both would answer the key.
        const string CoopGuid = "com.sailwindcoop.mod";
        static readonly Version CoopMinimum = new Version(0, 4, 0);

        private void Awake()
        {
            Log = Logger;

            // Stand down rather than fight an older co-op. Doing nothing leaves that player with co-op's own
            // body and menu, which work; loading anyway would leave them with a visibly broken game. They get
            // everything here the moment they update co-op.
            PluginInfo coop;
            if (Chainloader.PluginInfos.TryGetValue(CoopGuid, out coop)
                && coop.Metadata != null && coop.Metadata.Version < CoopMinimum)
            {
                Log.LogWarning($"Sailwind Co-op {coop.Metadata.Version} has its own player body and pause menu, " +
                    $"so this mod is standing down to avoid showing you two of each. Update Sailwind Co-op to " +
                    $"{CoopMinimum} or newer and this mod takes over.");
                return;
            }

            BodyTuning.Bind(Config);
            HeldToolPose.Bind(Config);

            // Always added. The body is only drawn in the ship-orbit camera, which is already something the
            // player chooses to switch to, so there is nothing to opt out of - and it is the only honest
            // source of "how many part variants does THIS machine have", which a character screen needs.
            gameObject.AddComponent<LocalBody>();
            // The parchment pause menu, the character screen, and the per-frame work both need.
            gameObject.AddComponent<MenuDriver>();
            new HarmonyLib.Harmony(PluginGuid).PatchAll(typeof(PauseMenuPatches).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded");
        }
    }
}
