using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The game's PlayerHeadSmoothing copies the whole local position of a mirror under the physics controller onto
    /// the head (cam), and only ever rewrites the mirror's world height. Aboard, the controller is tilted by the
    /// boat's heel at that point, so a height change (stepping onto something, the game popping you up when you push
    /// against an obstacle) also moves the mirror sideways in its own frame, and nothing moves it back. The eye ends
    /// up off the axis the view turns about, and turning on the spot swings the view. The mirror only needs its
    /// height. Registered by GuardedPatches on PlayerHeadSmoothing.LateUpdate.
    /// </summary>
    internal static class HeadSmoothingOnAxis
    {
        private static bool _logged, _failed;

        internal static void Postfix(PlayerHeadSmoothing __instance)
        {
            if (_failed) return;
            try
            {
                Transform mirror = __instance.walkColMirror;
                if (mirror == null) return;
                Vector3 local = mirror.localPosition;
                if (local.x == 0f && local.z == 0f) return;
                if (!_logged && local.x * local.x + local.z * local.z > 0.02f * 0.02f)
                {
                    _logged = true;
                    Plugin.Log.LogInfo($"[Camera] head smoothing was {new Vector2(local.x, local.z).magnitude:F2} m off the body's axis; put back");
                }
                local.x = 0f;
                local.z = 0f;
                mirror.localPosition = local;
                __instance.transform.localPosition = local;
            }
            catch (System.Exception e)
            {
                _failed = true;
                Plugin.Log.LogError("[Camera] head smoothing: " + e);
            }
        }
    }
}
