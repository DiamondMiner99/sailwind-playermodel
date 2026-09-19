using System;
using System.IO;
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
        public const string PluginVersion = "0.1.5";

        // Set here rather than in Awake: other mods can call the public API from their own Awake, which may run
        // before this one. A bare 'Logger' inside this class is the inherited instance property, hence the full name.
        public static ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(PluginName);

        // Sailwind Co-op carried its own copy of the body and the pause menu until this version. Running
        // that alongside this mod puts two sailors on the deck and two parchment scrolls on Escape, because
        // both would clone a panel and both would answer the key.
        const string CoopGuid = "com.sailwindcoop.mod";
        static readonly Version CoopMinimum = new Version(0, 4, 0);

        private void Awake()
        {
            // Stand down rather than fight an older co-op. Doing nothing leaves that player with co-op's own
            // body and menu, which work; loading anyway would leave them with a visibly broken game. They get
            // everything here the moment they update co-op. Nothing is bound or patched when standing down.
            Version coop = null;
            try
            {
                coop = InstalledCoopVersion();
            }
            catch (Exception e)
            {
                Log.LogWarning("Could not read the installed Sailwind Co-op version, loading normally: " + e.Message);
            }
            if (coop != null && coop < CoopMinimum)
            {
                Log.LogWarning($"Sailwind Co-op {coop} has its own player body and pause menu, " +
                    $"so this mod is standing down to avoid showing you two of each. Update Sailwind Co-op to " +
                    $"{CoopMinimum} or newer and this mod takes over.");
                return;
            }

            BodyTuning.Bind(Config);
            HeldToolPose.Bind(Config);
            InteractionTuning.Bind(Config);
            ItemPoseTuning.Bind(Config);
            SeatingTuning.Bind(Config);
            CameraTuning.Bind(Config);
            DownedTuning.Bind(Config);
            RemoveRetiredSettings();

            gameObject.AddComponent<Seating>();
            gameObject.AddComponent<Downed>();
            // Always added. The body is only drawn in the ship-orbit camera, which is already something the
            // player chooses to switch to, so there is nothing to opt out of - and it is the only honest
            // source of "how many part variants does THIS machine have", which a character screen needs.
            gameObject.AddComponent<LocalBody>();
            // The parchment pause menu, the character screen, and the per-frame work both need.
            gameObject.AddComponent<MenuDriver>();
            var harmony = new HarmonyLib.Harmony(PluginGuid);
            // Before PatchAll, so a PatchAll failure elsewhere cannot skip these. The try only matters if the
            // registration method itself cannot be compiled (a game type it names is gone); PatchAll still runs.
            try
            {
                GuardedPatches.Apply(harmony);
            }
            catch (Exception e)
            {
                Log.LogError("[Patches] none of the guarded patches were applied: " + e);
            }
            harmony.PatchAll(typeof(PauseMenuPatches).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded");
        }

        /// <summary>
        /// The highest Sailwind Co-op version in the plugins folder, or null when it cannot be told. BepInEx lists a
        /// plugin in Chainloader.PluginInfos only as it loads it, and this mod loads before co-op, so this reads the
        /// chainloader's own scan of the plugins folder from its cache instead. Kept out of Awake so that a missing
        /// BepInEx member surfaces at the call, inside Awake's try.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static Version InstalledCoopVersion()
        {
            // Null when [Caching] EnableAssemblyCache is off in BepInEx.cfg.
            var cache = TypeLoader.LoadAssemblyCache<PluginInfo>("chainloader");
            if (cache == null)
            {
                // Say so once. Without the cache this check is blind, so a player running an older co-op
                // gets two bodies and two menus with nothing in the log to explain it.
                Log.LogWarning("Could not read BepInEx's plugin cache, most likely because [Caching] " +
                    "EnableAssemblyCache is false in BepInEx.cfg. An older Sailwind Co-op cannot be " +
                    "detected that way, so if you end up with two bodies and two pause menus, update " +
                    $"Sailwind Co-op to {CoopMinimum} or newer (or turn EnableAssemblyCache back on).");
                return null;
            }
            Version best = null;
            foreach (var kv in cache)
            {
                var items = kv.Value != null ? kv.Value.CacheItems : null;
                if (items == null) continue;
                foreach (var info in items)
                {
                    if (info == null || info.Metadata == null || info.Metadata.Version == null
                        || info.Metadata.GUID != CoopGuid) continue;
                    if (!File.Exists(kv.Key)) continue; // a deleted DLL is not loaded
                    // Trust the entry only while its file is unchanged, the same test the chainloader applies. The
                    // cache save fails silently, so a stale entry can describe a co-op the player has since updated.
                    if (File.GetLastWriteTimeUtc(kv.Key).Ticks != kv.Value.Timestamp) return null;
                    // The chainloader loads the highest version of a GUID.
                    if (best == null || info.Metadata.Version > best) best = info.Metadata.Version;
                }
            }
            return best;
        }

        /// <summary>
        /// Settings this mod no longer reads. BepInEx never deletes a line a mod stops binding: it keeps it in a
        /// private list and writes it back on every save, so a retired setting would stay in the file forever
        /// looking like it does something. Only these exact names are removed.
        /// </summary>
        private void RemoveRetiredSettings()
        {
            var retired = new[]
            {
                new ConfigDefinition("1. Pose", "ShowOwnBody"),
                new ConfigDefinition("2. Held Tool", "HoldDistance"),
                new ConfigDefinition("2. Held Tool", "HoldDrop"),
                new ConfigDefinition("2. Held Tool", "HoldSide"),
                new ConfigDefinition("2. Held Tool", "GripX"),
                new ConfigDefinition("2. Held Tool", "GripY"),
                new ConfigDefinition("2. Held Tool", "GripZ"),
                new ConfigDefinition("2. Held Tool", "GripPitch"),
                new ConfigDefinition("2. Held Tool", "GripYaw"),
                new ConfigDefinition("2. Held Tool", "GripRoll"),
                new ConfigDefinition("5. Interactions", "HelmGripRadius"),
                new ConfigDefinition("5. Interactions", "HelmFollow"),
                new ConfigDefinition("5. Interactions", "CrankGripRadius"),
                new ConfigDefinition("5. Interactions", "CrankTwoHands"),
                new ConfigDefinition("7. Seating", "BodyFadeLength"),
            };
            try
            {
                var orphans = HarmonyLib.Traverse.Create(Config).Property("OrphanedEntries")
                    .GetValue<System.Collections.Generic.Dictionary<ConfigDefinition, string>>();
                if (orphans == null) return;
                int removed = 0;
                foreach (var d in retired) if (orphans.Remove(d)) removed++;
                if (removed == 0) return;
                Config.Save();
                Log.LogInfo($"Removed {removed} retired setting(s) from the config file");
            }
            catch (Exception e)
            {
                Log.LogWarning("Could not remove retired settings from the config file: " + e.Message);
            }
        }
    }
}
