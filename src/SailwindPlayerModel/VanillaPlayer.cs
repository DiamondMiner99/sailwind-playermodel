using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Reads of the vanilla local player that a body has to agree with: how far the controller origin sits
    /// above the soles, how far into a crouch the player is, and where they are looking.
    ///
    /// These live in ONE place on purpose. Every one of them used to exist as a byte-identical copy in the
    /// third-person body and in the co-op mod's networked avatar - refuted comments included - and that is
    /// exactly how the look-pitch defect below came to exist in two places at once. A player's own body and
    /// the body their crewmates are looking at must never be able to disagree about the same fact.
    /// </summary>
    public static class VanillaPlayer
    {
        /// <summary>
        /// Vertical distance (meters) from the CharacterController ORIGIN (which Refs.observerMirror tracks,
        /// roughly the capsule center) down to the capsule bottom, i.e. the feet: height/2 - center.y.
        ///
        /// Derived from live controller geometry rather than a constant so it stays correct across the runtime
        /// height scaling PlayerEmbarkerNew applies when boarding (col.height = initialHeight * 1.05).
        /// Returns 0 if the controller is not available, which reads as "no shift" everywhere it is used.
        /// </summary>
        public static float ControllerFeetGap()
        {
            var cc = Refs.charController;
            if (cc == null) return 0f;
            return cc.height * 0.5f - cc.center.y;
        }

        /// <summary>
        /// Normalized 0..1 crouch amount for the local player, sampled from the vanilla PlayerCrouching
        /// head-height lerp (standing initialHeight down to a crouched 0.2). Reading the lerped AMOUNT rather
        /// than a crouching bool is what lets a 20Hz networked avatar reproduce the smooth transition.
        /// Returns 0 when the component or height is not available yet (pre-load, degenerate rig).
        /// </summary>
        public static float Crouch01()
        {
            if (_crouching == null)
            {
                var rig = Refs.ovrCameraRig;
                if (rig != null) _crouching = rig.GetComponent<PlayerCrouching>();
                if (_crouching == null) return 0f;
                // Private field, set once in PlayerCrouching.Awake (= rig localPosition.y while standing).
                _standingHeight = Traverse.Create(_crouching).Field("initialHeight").GetValue<float>();
            }
            // Degenerate standing height (component not initialized, or a rig where standing about equals
            // crouched): treat as not crouching rather than emitting garbage.
            if (_standingHeight <= 0.3f) return 0f;
            float head = _crouching.GetCurrentHeadHeight();
            // currentHeadHeight starts at 0 and only lerps while GameState.playing, so a raw 0 would normalize
            // to FULL crouch. Treat the uninitialized band as standing; the real crouched endpoint is 0.2 and
            // the lerp approaches it from above.
            if (head < 0.1f) return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(_standingHeight, 0.2f, head));
        }

        /// <summary>
        /// The local player's clamped vertical look angle in degrees (about [-60,60]; positive = looking UP),
        /// read from the vanilla MouseLook.rotationY private field.
        ///
        /// Resolved by IDENTITY - the MouseLook on Refs.ovrCameraRig, which is the player head's vertical
        /// look. It used to scan every MouseLook in the scene and take the LARGEST ABSOLUTE rotationY, on the
        /// stated assumption that only the vertical head instance is ever non-zero. That assumption is false
        /// and it produced a reported bug: a crewmate's avatar was seen folded fully forward for a whole
        /// session, and only recovered when they toggled to the orbit camera and back.
        ///
        /// Vanilla has at least FIVE MouseLook instances (player yaw, player pitch, the bed/TrackingSpace
        /// look, BoatCamera orbit yaw, BoatCamera orbit pitch). rotationY is a private accumulator written
        /// ONLY in MouseLook.Update, so an instance that gets enabled=false (BoatCamera.SwitchOff, the
        /// shipyard rotator) FREEZES its last value forever - nothing in vanilla or any of these mods ever
        /// resets it. A parked orbit pitch sitting at its -60 clamp therefore wins the max-abs contest
        /// permanently and decodes to a roughly 54 degree spine fold against the 55 degree cap: visually
        /// maxed. The camera toggle "fixed" it only because it made that instance live-driven again.
        ///
        /// DO NOT "improve" this by filtering on ml.enabled - that INVERTS the bug. During the orbit camera
        /// the player looks are DISABLED and the boat looks ENABLED, so an enabled-filter would make the body
        /// mirror the orbit camera's pitch instead of holding the player's last first-person pitch. The
        /// invariant to preserve: while the orbit cam is on, the pitch stays the player's head pitch.
        ///
        /// Fails SAFE: if the head MouseLook cannot be resolved this returns 0 (no lean, neutral spine) rather
        /// than guessing from another instance. A missing lean is a cosmetic nothing; a wrong one is the bug
        /// above.
        /// </summary>
        public static float HeadLookPitchDeg()
        {
            if (_headMouseLook == null)
            {
                // Throttle the re-resolve: during menus and loading the rig does not exist, and this is called
                // from per-frame and 20Hz paths.
                float now = Time.realtimeSinceStartup;
                if (now < _nextMouseLookScan) return 0f;

                var rig = Refs.ovrCameraRig;
                _headMouseLook = rig != null ? rig.GetComponent<MouseLook>() : null;
                if (_headMouseLook == null)
                {
                    _nextMouseLookScan = now + MouseLookRescanInterval;
                    return 0f;
                }
            }
            return MouseLookRotationYRef(_headMouseLook);
        }

        private static PlayerCrouching _crouching;
        private static float _standingHeight = -1f;

        /// <summary>The player head's vertical MouseLook. Null until resolved / after a scene change.</summary>
        private static MouseLook _headMouseLook;
        private static float _nextMouseLookScan;
        private const float MouseLookRescanInterval = 1.5f;

        private static readonly AccessTools.FieldRef<MouseLook, float> MouseLookRotationYRef =
            AccessTools.FieldRefAccess<MouseLook, float>("rotationY");
    }
}
