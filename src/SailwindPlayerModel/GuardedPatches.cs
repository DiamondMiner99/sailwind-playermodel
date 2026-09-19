using System;
using HarmonyLib;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Harmony patches applied one at a time instead of through PatchAll. PatchAll stops at the first class whose
    /// target is missing, so a game update that renames one method would take down every patch after it, the camera
    /// and the pause menu included. These classes carry no [HarmonyPatch] attribute, so PatchAll ignores them; a patch
    /// whose game method is gone is logged and skipped, and the rest of the mod still patches.
    /// </summary>
    internal static class GuardedPatches
    {
        internal static void Apply(Harmony harmony)
        {
            Try(harmony, typeof(PlayerSwimming), "LateUpdate", Type.EmptyTypes,
                typeof(FollowCameraSwimming), "Prefix", "Postfix");
            Try(harmony, typeof(GPButtonSettingsCheckbo), "UpdateButton", Type.EmptyTypes,
                typeof(InvertMouseCheckbox), "Prefix", "Postfix");
            Try(harmony, typeof(GoPointer), "LateUpdate", Type.EmptyTypes,
                typeof(PointerPlaced), null, "Postfix");
            Try(harmony, typeof(StartMenu), "FadeStartMenu", new[] { typeof(int) },
                typeof(PauseMenuPatches.InGameTitleFadePatch), "Prefix", null);
        }

        // The prefix and postfix of one patch go on in a single Patch call, and both are looked up before it, so a
        // patch is applied whole or not at all.
        private static void Try(Harmony harmony, Type target, string method, Type[] args, Type patches,
            string prefix, string postfix)
        {
            string name = target.Name + "." + method;
            try
            {
                var original = AccessTools.Method(target, method, args);
                if (original == null)
                {
                    Plugin.Log.LogWarning($"[Patches] {name} not found, that patch is skipped");
                    return;
                }
                var pre = prefix != null ? AccessTools.Method(patches, prefix) : null;
                var post = postfix != null ? AccessTools.Method(patches, postfix) : null;
                if ((prefix != null && pre == null) || (postfix != null && post == null))
                {
                    Plugin.Log.LogWarning($"[Patches] {patches.Name} is missing its patch method, the patch on {name} is skipped");
                    return;
                }
                harmony.Patch(original,
                    pre != null ? new HarmonyMethod(pre) : null,
                    post != null ? new HarmonyMethod(post) : null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Patches] {name} skipped: {e}");
            }
        }
    }
}
