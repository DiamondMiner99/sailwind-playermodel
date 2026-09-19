using System;
using System.Collections;
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

        // Set when GameToSettings pauses, cleared by SettingsToGame. Owned here rather than read from
        // GameState.inCursorMenu or wasInSettingsMenu, which other mods and an end-of-frame coroutine write.
        static bool _pausedByMenu;

        // Built once at boot; the panel exists by Start.
        [HarmonyPatch(typeof(StartMenu), "Start")]
        public static class StartMenuStartPatch
        {
            static void Postfix(StartMenu __instance)
            {
                _pausedByMenu = false; // a fresh scene starts unpaused
                ActiveStartMenu = __instance;
                ModPauseMenu.Install(__instance);
            }
        }

        // In-game pause opens via GameToSettings, which does all the pause bookkeeping and opens the vanilla
        // settings panel. Postfix: hide that and show ours instead.
        [HarmonyPatch(typeof(StartMenu), "GameToSettings")]
        public static class GameToSettingsPatch
        {
            static bool Prefix(StartMenu __instance)
            {
                if (_pausedByMenu && Time.timeScale <= 0f)
                {
                    // Already paused with time stopped. Running GameToSettings again stores timeScale 0 as the
                    // value Resume restores, which freezes the game for good. Harmony still runs the postfix,
                    // which shows the parchment again. Another mod can lock the cursor while the game is
                    // paused (Three Sheets does when the F1 window closes), so free it the way the pause does.
                    if (!GameState.inCursorMenu) MouseLook.ToggleMouseLookAndCursor(false);
                    // Mouse look was live while the cursor was locked, so the view may have turned away from
                    // where the first pause placed the menu, and LatePin does not re-aim at timeScale 0. Aim
                    // it the way GameToSettings does. MoveMenuToPlayer only moves the menu's transform.
                    try { Traverse.Create(__instance).Method("MoveMenuToPlayer").GetValue(); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("[PauseMenu] Could not move the menu in front of the view: " + e.GetBaseException().Message);
                    }
                    Plugin.Log.LogWarning("[PauseMenu] Pause requested while already paused, showing the menu again instead");
                    return false;
                }
                _pausedByMenu = true;
                return true;
            }

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
                _pausedByMenu = false;
                ModPauseMenu.Hide();
                ModPauseMenu.SubPageFromPause = false;
            }
        }

        // In game, SettingsToGame calls DisableStartMenu, which starts FadeStartMenu(-1) on the title scroll even
        // though it is already hidden. That coroutine holds animsPlaying at 1 across a scaled 0.2 s wait, and
        // StartMenu.ButtonClick ignores every click while it is held, so pausing again inside that window left
        // Quit and Recover dead for the whole pause. Skip the fade only when there is nothing to move.
        // DisableStartMenu still sets the logo scale and position.
        //
        // No [HarmonyPatch] attribute: FadeStartMenu is a private coroutine, so this is registered on its own
        // through GuardedPatches, and a renamed method skips only this patch.
        internal static class InGameTitleFadePatch
        {
            static bool Prefix(StartMenu __instance, int __0, ref IEnumerator __result)
            {
                if (!GameState.playing || __0 != -1) return true;
                GameObject ui = null;
                // Traverse rather than an injected ___startUI, so a renamed field falls back to vanilla
                // instead of failing the patch.
                try { ui = Traverse.Create(__instance).Field("startUI").GetValue<GameObject>(); } catch { }
                if (ui == null || ui.activeSelf) return true; // fail open to vanilla
                __result = Nothing();
                return false;
            }

            static IEnumerator Nothing() { yield break; }
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
                // GameToSettingsPatch's prefix now refuses a second pause while time is stopped, as the
                // backstop for any blank paused screen; these claims still keep the key away from vanilla.
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

        // WindSound.Update guards Time.timeScale but not Time.deltaTime. When ModPauseMenu restores the
        // timescale from inside the paused frame (KeepWorldRunning, so a co-op host keeps simulating), that
        // frame still has deltaTime 0: the guard passes, the position delta is divided by zero, and with a
        // stationary player 0/0 puts NaN into apparentWind. Lerp never recovers from a NaN endpoint, so every
        // pitch write after that is refused and Unity logs "Attempt to set pitch to infinite value" once per
        // frame for the rest of the session. Vanilla never sees this because its own restore runs after
        // WindSound.Update in the frame. A zero-delta frame has nothing to integrate, so skipping it is safe.
        [HarmonyPatch(typeof(WindSound), "Update")]
        public static class WindSoundZeroDeltaPatch
        {
            static bool Prefix()
            {
                return Time.deltaTime > 0f;
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
                {
                    if (t.name == ModPauseMenu.TemplateName) return false; // never run vanilla New Game from the kept template
                    if (ModPauseMenu.HandleClick(t.name)) return false;
                }
                return true; // not ours, run the vanilla action
            }
        }
    }
}
