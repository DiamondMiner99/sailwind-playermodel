using System;
using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Wires <see cref="ModPauseMenu"/> into the game's world-space StartMenu: build it at boot, swap it in
    /// for the vanilla settings panel when the game pauses, hide it on unpause, and route Escape and clicks.
    /// </summary>
    public static class PauseMenuPatches
    {
        /// <summary>The live StartMenu, captured at Start. Other mods need it to drive vanilla menu flows.</summary>
        public static StartMenu ActiveStartMenu { get; private set; }

        // Built once at boot; the panel exists by Start.
        [HarmonyPatch(typeof(StartMenu), "Start")]
        public static class StartMenuStartPatch
        {
            static void Postfix(StartMenu __instance)
            {
                ActiveStartMenu = __instance;
                ModPauseMenu.Install(__instance);
            }
        }

        // In-game pause opens via GameToSettings, which does all the pause bookkeeping and opens the vanilla
        // settings panel. Postfix: hide that and show ours instead.
        [HarmonyPatch(typeof(StartMenu), "GameToSettings")]
        public static class GameToSettingsPatch
        {
            static void Postfix(StartMenu __instance)
            {
                ModPauseMenu.OnPauseOpened(__instance);
            }
        }

        // Any unpause (Resume, Escape, settings-Back-to-game) routes through SettingsToGame. The StartMenu
        // root never deactivates, so the panel has to be hidden here or it lingers during gameplay.
        [HarmonyPatch(typeof(StartMenu), "SettingsToGame")]
        public static class SettingsToGamePatch
        {
            static void Postfix()
            {
                ModPauseMenu.Hide();
                ModPauseMenu.SubPageFromPause = false;
            }
        }

        // While our panel (or a sub-page opened from it) is up, route Escape to Resume or back-to-pause
        // instead of the vanilla open-settings path, which does not recognize our panel and would re-pause.
        [HarmonyPatch(typeof(StartMenu), "LateUpdate")]
        public static class StartMenuLateUpdatePatch
        {
            static bool Prefix(StartMenu __instance)
            {
                if (!GameState.playing) return true;
                if (!(Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10) || Input.GetKeyDown(KeyCode.JoystickButton6)))
                    return true;

                // An IMGUI screen drawn over the menu owns the pause key while it is up, and for the rest of
                // the frame in which it swallowed one. Both terms are load-bearing:
                //   IsOpen - the screen can be drawn while its Tick is not running, so the vanilla re-pause
                //     still has to be blocked; the panel's own Done button remains the way out.
                //   ConsumedPauseKeyThisFrame - stops the same press from also reaching vanilla and
                //     unpausing after the screen closed itself.
                // Without this, vanilla sees no active panel (ours hidden by the screen's Open, its own
                // settingsUI disabled by OnPauseOpened) and re-enters GameToSettings while ALREADY paused,
                // latching unpausedTimescale = 0, which is a permanent freeze on the next resume.
                if (CharacterScreen.IsOpen || CharacterScreen.ConsumedPauseKeyThisFrame) return false;
                if (PauseKeyClaimed()) return false;

                // Returns true if it handled it (resume, or settings-back-to-pause), so skip vanilla.
                if (ModPauseMenu.OnEscape(__instance)) return false;

                return true; // run vanilla LateUpdate (closes the open cursor-menu, or opens pause)
            }
        }

        /// <summary>
        /// Other mods' screens claim the pause key the same way ours does. Co-op adds its friends screen and
        /// message panel here. Never throws: a claimant that fails is treated as not claiming.
        /// </summary>
        public static Func<bool> ExtraPauseKeyClaim;

        static bool PauseKeyClaimed()
        {
            var f = ExtraPauseKeyClaim;
            if (f == null) return false;
            try { return f(); } catch { return false; }
        }

        // Settings sub-page "Back": if it was opened from our pause menu, return to our panel instead of
        // unpausing. (__0 is the StartMenuButtonType argument, named positionally so it is name-agnostic.)
        [HarmonyPatch(typeof(StartMenu), "ButtonClick", new[] { typeof(StartMenuButtonType) })]
        public static class ButtonClickPatch
        {
            static bool Prefix(StartMenu __instance, StartMenuButtonType __0)
            {
                return !ModPauseMenu.OnSettingsBack(__instance, __0);
            }
        }

        // Route clicks on our cloned buttons. The StartMenuButton sits on a 'bg+trigger' child while our
        // marker name is on an ANCESTOR, so walk up. A misread would fire the vanilla action instead.
        [HarmonyPatch(typeof(StartMenuButton), "OnActivate", new Type[0])]
        public static class StartMenuButtonOnActivatePatch
        {
            static bool Prefix(StartMenuButton __instance)
            {
                for (var t = __instance.transform; t != null; t = t.parent)
                    if (ModPauseMenu.HandleClick(t.name)) return false;
                return true; // not ours, run the vanilla action
            }
        }
    }
}
