using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The game's mouth trigger, carried on the pointer while the follow view or the game's ship view is up.
    ///
    /// Eating, drinking and smoking are trigger contacts between the held item and 'mouth col trigger' (MouthCol), which
    /// hangs 0.25 m under the eye. GoPointer places the held item from the pointer, and in first person the pointer sits
    /// on the eye with no offset. BoatCamera.SwitchOn leaves the pointer where it is (moved onto the body) but sends the
    /// eye, and the mouth with it, out to the camera rig, so nothing held reached the mouth and no bite, drink or puff
    /// registered in the follow view or the ship view. On the pointer at the same local pose, the trigger sits where it
    /// does in first person relative to everything the pointer holds.
    ///
    /// The one trigger the game made is moved, never copied: MouthCol.Awake writes Refs.playerMouthCol, so a second one
    /// would take it over.
    /// </summary>
    internal static class MouthOnPointer
    {
        private static MouthCol _mouth;
        private static Transform _home;
        private static Vector3 _localPos, _localScale;
        private static Quaternion _localRot;
        private static bool _onPointer, _failed;

        /// <summary>
        /// Put the mouth trigger on the pointer (<paramref name="on"/>) or back under the eye. Cheap when nothing changes.
        /// A failure puts it back under the eye and leaves it there for the session.
        /// </summary>
        internal static void Sync(bool on)
        {
            if (_failed) return;
            try
            {
                var mouth = Refs.playerMouthCol;
                if (mouth == null) return;
                if (mouth != _mouth)
                {
                    // First sight, or a new scene made a new one. Nothing has moved it yet.
                    _mouth = mouth;
                    Transform m = mouth.transform;
                    _home = m.parent;
                    _localPos = m.localPosition;
                    _localRot = m.localRotation;
                    _localScale = m.localScale;
                    _onPointer = false;
                }

                Transform pointer = null;
                if (on)
                {
                    var p = LocalInteraction.Pointer;
                    if (p != null && p.type == GoPointer.PointerType.crosshairMouse) pointer = p.transform;
                }
                Transform want = pointer != null ? pointer : _home;
                if (want == null) return;
                Transform t = _mouth.transform;
                if (t.parent == want) return;
                PutUnder(t, want);
                bool nowOnPointer = want == pointer;
                if (nowOnPointer != _onPointer)
                {
                    _onPointer = nowOnPointer;
                    Plugin.Log.LogInfo(nowOnPointer ? "[Items] mouth follows the pointer" : "[Items] mouth back at the eye");
                }
            }
            catch (System.Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning("[Items] mouth trigger left at the eye: " + e);
                try
                {
                    if (_mouth != null && _home != null && _mouth.transform.parent != _home) PutUnder(_mouth.transform, _home);
                }
                catch { }
            }
        }

        // The pointer sits on the eye with no offset in first person, so the same local pose under either one is the
        // game's own place for the trigger.
        private static void PutUnder(Transform t, Transform parent)
        {
            t.SetParent(parent, false);
            t.localPosition = _localPos;
            t.localRotation = _localRot;
            t.localScale = _localScale;
        }
    }
}
