using BepInEx.Configuration;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>How a body shows the item it is holding.</summary>
    public enum HeldPoseMode
    {
        /// <summary>No arm pose; the item stays wherever its owner put it.</summary>
        Off,
        /// <summary>The right arm reaches toward the item where it really is (about 1.15m in front of the
        /// holder's eyes, so usually out of reach: reads as pointing).</summary>
        HandReachesItem,
        /// <summary>The item is drawn in the right hand and the arm aims it the way the holder is aiming it.
        /// Cosmetic on the viewer only.</summary>
        ItemInHand,
    }

    /// <summary>
    /// Tuning for a body visibly holding a tool. Every value is a live config entry so the pose can be tuned
    /// in game from the F1 Configuration Manager while watching the result.
    /// </summary>
    public static class HeldToolPose
    {
        private const string Section = "2. Held Tool";

        public static ConfigEntry<HeldPoseMode> Mode { get; private set; }
        public static ConfigEntry<float> HoldDistance { get; private set; }
        public static ConfigEntry<float> HoldDrop { get; private set; }
        public static ConfigEntry<float> HoldSide { get; private set; }
        public static ConfigEntry<float> ElbowDown { get; private set; }
        public static ConfigEntry<float> ElbowOut { get; private set; }
        public static ConfigEntry<float> BlendSpeed { get; private set; }
        public static ConfigEntry<float> GripX { get; private set; }
        public static ConfigEntry<float> GripY { get; private set; }
        public static ConfigEntry<float> GripZ { get; private set; }
        public static ConfigEntry<float> GripPitch { get; private set; }
        public static ConfigEntry<float> GripYaw { get; private set; }
        public static ConfigEntry<float> GripRoll { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            Mode = cfg.Bind(Section, "Mode", HeldPoseMode.ItemInHand,
                "How a body shows what it holds. ItemInHand: the item sits in the right hand, aimed where the holder aims it. HandReachesItem: the arm reaches for the item where it really floats. Off: no arm pose.");
            HoldDistance = cfg.Bind(Section, "HoldDistance", 0.45f,
                new ConfigDescription("ItemInHand: how far in front of the head the hand goes, along the direction the holder is aiming (meters).",
                    new AcceptableValueRange<float>(0.1f, 0.9f)));
            HoldDrop = cfg.Bind(Section, "HoldDrop", -0.2f,
                new ConfigDescription("ItemInHand: hand height relative to that aim point (meters; negative = lower).",
                    new AcceptableValueRange<float>(-0.6f, 0.3f)));
            HoldSide = cfg.Bind(Section, "HoldSide", 0.12f,
                new ConfigDescription("ItemInHand: sideways hand offset (meters; positive = toward the holder's right).",
                    new AcceptableValueRange<float>(-0.4f, 0.4f)));
            ElbowDown = cfg.Bind(Section, "ElbowDown", 1.0f,
                new ConfigDescription("Elbow direction: how strongly the elbow points down.",
                    new AcceptableValueRange<float>(-1f, 2f)));
            ElbowOut = cfg.Bind(Section, "ElbowOut", 0.5f,
                new ConfigDescription("Elbow direction: how strongly the elbow points out to the side.",
                    new AcceptableValueRange<float>(-1f, 2f)));
            BlendSpeed = cfg.Bind(Section, "BlendSpeed", 8f,
                new ConfigDescription("How fast the arm raises into the hold pose and drops out of it.",
                    new AcceptableValueRange<float>(1f, 30f)));
            GripX = cfg.Bind(Section, "GripX", 0f,
                new ConfigDescription("ItemInHand: item offset from the hand along the item's own X axis (meters).",
                    new AcceptableValueRange<float>(-0.4f, 0.4f)));
            GripY = cfg.Bind(Section, "GripY", 0f,
                new ConfigDescription("ItemInHand: item offset from the hand along the item's own Y axis (meters).",
                    new AcceptableValueRange<float>(-0.4f, 0.4f)));
            GripZ = cfg.Bind(Section, "GripZ", 0f,
                new ConfigDescription("ItemInHand: item offset from the hand along the item's own Z axis (meters).",
                    new AcceptableValueRange<float>(-0.4f, 0.4f)));
            GripPitch = cfg.Bind(Section, "GripPitch", 0f,
                new ConfigDescription("ItemInHand: extra item rotation about its X axis (degrees).",
                    new AcceptableValueRange<float>(-180f, 180f)));
            GripYaw = cfg.Bind(Section, "GripYaw", 0f,
                new ConfigDescription("ItemInHand: extra item rotation about its Y axis (degrees).",
                    new AcceptableValueRange<float>(-180f, 180f)));
            GripRoll = cfg.Bind(Section, "GripRoll", 0f,
                new ConfigDescription("ItemInHand: extra item rotation about its Z axis (degrees).",
                    new AcceptableValueRange<float>(-180f, 180f)));
        }
    }

    /// <summary>
    /// Two-bone arm IK for one arm, run after the walk gait has posed it. Same method as the crouch leg IK in
    /// <see cref="SyntyBody"/>: law of cosines for the elbow, a pole vector to pick which way the elbow bends,
    /// and each bone turned so its captured bone-to-child LOCAL aim axis points at its solved target, so
    /// nothing depends on the rig's local axis conventions. A reach-limited target is clamped onto the arm's
    /// sphere instead of snapping or over-extending.
    /// </summary>
    public sealed class ArmIk
    {
        private Transform _upper, _fore, _hand;
        private float _upperLen, _foreLen;
        private Vector3 _upperAimLocal, _foreAimLocal;
        private float _weight;

        public bool Ready { get; private set; }
        public Transform Hand { get { return _hand; } }

        /// <summary>
        /// Capture bone lengths and aim axes. The aim axes are the child's direction in the bone's own frame,
        /// which does not depend on the current pose, so this can run from any frame. Lengths are in world
        /// units, so call it once the body has its final scale.
        /// </summary>
        public bool Capture(Transform upper, Transform fore, Transform hand)
        {
            Ready = false;
            if (upper == null || fore == null || hand == null) return false;
            Vector3 u = fore.position - upper.position;
            Vector3 f = hand.position - fore.position;
            if (u.sqrMagnitude < 1e-6f || f.sqrMagnitude < 1e-6f) return false;

            _upper = upper; _fore = fore; _hand = hand;
            _upperLen = u.magnitude;
            _foreLen = f.magnitude;
            _upperAimLocal = upper.InverseTransformDirection(u / _upperLen);
            _foreAimLocal = fore.InverseTransformDirection(f / _foreLen);
            _weight = 0f;
            Ready = true;
            return true;
        }

        /// <summary>
        /// Blend the arm toward the target by an eased weight (1 = on target, 0 = the gait pose untouched).
        /// While releasing, keep passing the last target so the arm lowers along the same path.
        /// </summary>
        public void Solve(Vector3 target, Vector3 poleHint, float weightTarget, float blendSpeed, float dt)
        {
            if (!Ready) return;
            // A rebuilt body (appearance change) destroys these bones before the next capture runs.
            if (_upper == null || _fore == null || _hand == null) { Ready = false; return; }
            _weight = Mathf.Lerp(_weight, weightTarget, 1f - Mathf.Exp(-blendSpeed * dt));
            if (_weight < 0.001f) return;

            Quaternion preUpper = _upper.localRotation;
            Quaternion preFore = _fore.localRotation;

            float a = _upperLen, b = _foreLen;
            Vector3 s = _upper.position;
            Vector3 st = target - s;
            float d = Mathf.Clamp(st.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            Vector3 dir = st.sqrMagnitude > 1e-8f ? st.normalized : Vector3.forward;

            float cosS = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float shoulderAngle = Mathf.Acos(cosS);

            Vector3 pole = poleHint - Vector3.Dot(poleHint, dir) * dir;
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.down - Vector3.Dot(Vector3.down, dir) * dir;
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.forward;
            pole.Normalize();

            Vector3 elbow = s + a * (Mathf.Cos(shoulderAngle) * dir + Mathf.Sin(shoulderAngle) * pole);
            Vector3 hand = s + dir * d; // reach-clamped target

            Vector3 wantUpper = elbow - s;
            if (wantUpper.sqrMagnitude > 1e-10f)
                _upper.rotation = Quaternion.FromToRotation(_upper.TransformDirection(_upperAimLocal), wantUpper.normalized) * _upper.rotation;

            Vector3 wantFore = hand - _fore.position;
            if (wantFore.sqrMagnitude > 1e-10f)
                _fore.rotation = Quaternion.FromToRotation(_fore.TransformDirection(_foreAimLocal), wantFore.normalized) * _fore.rotation;

            if (_weight < 0.999f)
            {
                _upper.localRotation = Quaternion.Slerp(preUpper, _upper.localRotation, _weight);
                _fore.localRotation = Quaternion.Slerp(preFore, _fore.localRotation, _weight);
            }
        }
    }
}
