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
        /// Cosmetic on the viewer only, except that with Interactions enabled the local player's barrel goes back
        /// to the two-handed carry after drinking from it.</summary>
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
        public static ConfigEntry<float> ElbowDown { get; private set; }
        public static ConfigEntry<float> ElbowOut { get; private set; }
        public static ConfigEntry<float> BlendSpeed { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            Mode = cfg.Bind(Section, "Mode", HeldPoseMode.ItemInHand,
                "How a body shows what it holds. ItemInHand: the item is drawn in the hands, held the way that item is held (see 6. Item Poses). HandReachesItem: the item stays where the game floats it and the hands reach for it. Off: no arm pose. With ItemInHand and 5. Interactions Enabled on, a barrel you drink from goes back to the two-handed carry when you stop drinking, in first person too, and like a barrel you have just picked up it cannot go into a crate or onto a shelf. Otherwise the game handles that barrel its own way.");
            ElbowDown = cfg.Bind(Section, "ElbowDown", 1.0f,
                new ConfigDescription("Elbow direction: how strongly the elbow points down.",
                    new AcceptableValueRange<float>(-1f, 2f)));
            ElbowOut = cfg.Bind(Section, "ElbowOut", 0.5f,
                new ConfigDescription("Elbow direction: how strongly the elbow points out to the side.",
                    new AcceptableValueRange<float>(-1f, 2f)));
            BlendSpeed = cfg.Bind(Section, "BlendSpeed", 8f,
                new ConfigDescription("How fast the arm raises into the hold pose and drops out of it.",
                    new AcceptableValueRange<float>(1f, 30f)));
        }
    }

    /// <summary>
    /// Two-bone arm IK for one arm, run after the walk gait has posed it. Same method as the crouch leg IK in
    /// <see cref="SyntyBody"/>: law of cosines for the elbow, a pole vector to pick which way the elbow bends,
    /// and each bone turned so its captured bone-to-child LOCAL aim axis points at its solved target, so
    /// nothing depends on the rig's local axis conventions. A reach-limited target is clamped onto the arm's
    /// sphere instead of snapping or over-extending.
    ///
    /// THE TARGET IS THE PALM, NOT THE WRIST. The Synty hand bone sits at the wrist and the palm is about
    /// 7 cm further along the fingers, so solving the wrist onto a handle left the hand hanging past it. The
    /// palm offset and the hand's own axes (fingers toward the knuckles, palm normal toward the finger curl)
    /// are captured from the finger bones, and the hand is turned to the grip's orientation after the arm
    /// is solved.
    /// </summary>
    public sealed class ArmIk
    {
        private Transform _upper, _fore, _hand;
        private float _upperLen, _foreLen;
        private Vector3 _upperAimLocal, _foreAimLocal;
        private float _weight;

        // Hand frame, in the hand bone's local space.
        private Vector3 _fingerLocal = Vector3.right;
        private Vector3 _palmNormalLocal = Vector3.up;
        private Vector3 _knuckleLocal = Vector3.forward;   // index knuckle toward little finger, across the fist
        private Vector3 _palmLocal;
        private float _axisSign = 1f;                       // which way round a bar the fist last closed

        public bool Ready { get; private set; }
        public Transform Hand { get { return _hand; } }
        /// <summary>The eased blend actually applied last solve: 0 = walk pose, 1 = on target.</summary>
        public float Weight { get { return _weight; } }
        /// <summary>Shoulder to wrist at full extension, in world units. 0 before capture.</summary>
        public float Length { get { return Ready ? _upperLen + _foreLen : 0f; } }
        /// <summary>Where the palm center is right now, in world space.</summary>
        public Vector3 PalmPosition { get { return _hand != null ? _hand.TransformPoint(_palmLocal) : Vector3.zero; } }
        /// <summary>
        /// How far past the wrist the palm target sits, in world units. SolveBones clamps the bone chain at the
        /// wrist, so a grip is really reachable out to <see cref="Length"/> plus this and no farther. 0 before capture.
        /// </summary>
        public float PalmReach { get { return Ready && _hand != null ? Vector3.Scale(_palmLocal, _hand.lossyScale).magnitude : 0f; } }

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
            CaptureHandFrame(f / _foreLen);
            Ready = true;
            return true;
        }

        /// <summary>
        /// Find the palm from the finger bones: the knuckles are the first joint of the middle-finger group and
        /// of the index finger, and the fingers curl toward the palm. Falls back to "fingers continue the
        /// forearm, palm 7 cm out" on a rig without finger bones.
        /// </summary>
        private void CaptureHandFrame(Vector3 forearmDirWorld)
        {
            Transform f1 = null, f2 = null, index1 = null;
            foreach (Transform c in _hand)
            {
                string n = c.name;
                if (n.StartsWith("Finger_01", System.StringComparison.Ordinal)) { f1 = c; if (c.childCount > 0) f2 = c.GetChild(0); }
                else if (n.StartsWith("IndexFinger_01", System.StringComparison.Ordinal)) index1 = c;
            }
            if (f1 != null && index1 != null && f2 != null)
            {
                Vector3 k1 = _hand.InverseTransformPoint(f1.position);
                Vector3 ki = _hand.InverseTransformPoint(index1.position);
                Vector3 knuckles = (k1 + ki) * 0.5f;
                Vector3 curl = _hand.InverseTransformPoint(f2.position) - k1;
                if (knuckles.sqrMagnitude > 1e-8f)
                {
                    Vector3 finger = knuckles.normalized;
                    Vector3 n = curl - Vector3.Dot(curl, finger) * finger;
                    Vector3 across = Vector3.ProjectOnPlane(k1 - ki, finger);
                    if (n.sqrMagnitude > 1e-10f && across.sqrMagnitude > 1e-10f)
                    {
                        _fingerLocal = finger;
                        _palmNormalLocal = n.normalized;
                        _knuckleLocal = across.normalized;
                        // The palm center: most of the way to the knuckles, and a little toward the palm side,
                        // which is where a gripped handle actually rests.
                        _palmLocal = knuckles * 0.72f + _palmNormalLocal * (knuckles.magnitude * 0.22f);
                        return;
                    }
                }
            }
            _fingerLocal = _hand.InverseTransformDirection(forearmDirWorld).normalized;
            Vector3 side = Vector3.Cross(_fingerLocal, Vector3.forward);
            _palmNormalLocal = side.sqrMagnitude > 1e-4f ? side.normalized : Vector3.up;
            _knuckleLocal = Vector3.Cross(_palmNormalLocal, _fingerLocal).normalized;
            float scale = Mathf.Max(Mathf.Abs(_hand.lossyScale.x), 1e-4f);
            _palmLocal = _fingerLocal * (0.07f / scale);
        }

        /// <summary>
        /// Turn the hand to hang relaxed off the forearm: fingers on along the arm and a little forward, palm toward
        /// <paramref name="inward"/> (the body's side). Run every frame before the grip solve. Nothing else resets the
        /// hand bone, so without this a hand kept whatever turn its last grip gave it after the arm let go, and the
        /// rig's own rest has the palms facing backward.
        /// </summary>
        public void RestHand(Vector3 inward, Vector3 forward)
        {
            if (!Ready || _hand == null || _fore == null) return;
            Vector3 forearm = _hand.position - _fore.position;
            if (forearm.sqrMagnitude < 1e-8f) return;
            _hand.rotation = HandRotationFor((forearm.normalized + forward * 0.15f).normalized, inward);
        }

        /// <summary>The world rotation that puts this hand's fingers along <paramref name="fingerDir"/> with the palm facing <paramref name="palmNormal"/>.</summary>
        public Quaternion HandRotationFor(Vector3 fingerDir, Vector3 palmNormal)
        {
            if (_hand == null || fingerDir.sqrMagnitude < 1e-8f || palmNormal.sqrMagnitude < 1e-8f)
                return _hand != null ? _hand.rotation : Quaternion.identity;
            Vector3 n = Vector3.ProjectOnPlane(palmNormal, fingerDir);
            if (n.sqrMagnitude < 1e-8f) n = Vector3.Cross(fingerDir, Vector3.right);
            return Quaternion.LookRotation(fingerDir.normalized, n.normalized) * Quaternion.Inverse(Quaternion.LookRotation(_fingerLocal, _palmNormalLocal));
        }

        /// <summary>Put the wrist on the target and leave the hand as the forearm carries it.</summary>
        public void Solve(Vector3 target, Vector3 poleHint, float weightTarget, float blendSpeed, float dt)
        {
            SolveWrist(target, poleHint, weightTarget, blendSpeed, dt);
        }

        /// <summary>
        /// Put the PALM on <paramref name="palmTarget"/> and close the hand the way the grip asks, with the elbow
        /// bent toward the pole.
        ///
        /// A grip with an <paramref name="axis"/> is a bar, handle or rope running through the fist: the knuckles
        /// line up with the bar and the fingers wrap round it, and of all the ways round the bar the one nearest
        /// the forearm is taken, so the wrist stays straight. A grip without one uses
        /// <paramref name="finger"/> and <paramref name="palmNormal"/> as given.
        ///
        /// THE HAND'S ROTATION IS SETTLED BEFORE THE WRIST IS PLACED. The wrist target is the palm target minus the
        /// palm offset in the hand's final rotation, so a rotation changed afterwards (by the wrist limit) would
        /// slide the palm off the handle and leave the handle at the wrist. It takes two passes, because the
        /// rotation depends on the forearm and the forearm on where the wrist goes; the second pass starts from
        /// the first pass's forearm and lands within a few millimeters.
        /// </summary>
        public void SolveGrip(Vector3 palmTarget, Vector3 finger, Vector3 palmNormal, Vector3 axis, Vector3 poleHint,
            float weightTarget, float blendSpeed, float dt, float maxWristBend)
        {
            if (!Ready) return;
            if (_upper == null || _fore == null || _hand == null) { Ready = false; return; }
            _weight = Mathf.Lerp(_weight, weightTarget, 1f - Mathf.Exp(-blendSpeed * dt));
            if (_weight < 0.001f) return;

            Quaternion preUpper = _upper.localRotation;
            Quaternion preFore = _fore.localRotation;
            Vector3 scale = _hand.lossyScale;

            Quaternion rot = GripRotation(finger, palmNormal, axis, palmTarget - _upper.position, maxWristBend);
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass > 0) { _upper.localRotation = preUpper; _fore.localRotation = preFore; }
                SolveBones(palmTarget - rot * Vector3.Scale(_palmLocal, scale), poleHint);
                rot = GripRotation(finger, palmNormal, axis, _hand.position - _fore.position, maxWristBend);
            }
            _hand.rotation = _weight >= 0.999f ? rot : Quaternion.Slerp(_hand.rotation, rot, _weight);
        }

        private Quaternion GripRotation(Vector3 finger, Vector3 palmNormal, Vector3 axis, Vector3 forearm, float maxWristBend)
        {
            Vector3 fa = forearm.sqrMagnitude > 1e-10f ? forearm.normalized : (finger.sqrMagnitude > 1e-10f ? finger.normalized : Vector3.forward);
            Quaternion rot;
            if (axis.sqrMagnitude > 1e-8f)
            {
                Vector3 a = axis.normalized;
                Vector3 f = Vector3.ProjectOnPlane(fa, a);
                if (f.sqrMagnitude < 0.02f && finger.sqrMagnitude > 1e-8f) f = Vector3.ProjectOnPlane(finger, a);
                if (f.sqrMagnitude < 1e-8f) f = Vector3.Cross(a, Mathf.Abs(a.y) < 0.9f ? Vector3.up : Vector3.right);
                f.Normalize();
                // Either way round the bar holds it; keep the one the fist already has unless the other is clearly
                // closer to how the hand is turned, so a hand does not flip over from one frame to the next.
                Quaternion same = FrameRotation(f, a * _axisSign);
                Quaternion other = FrameRotation(f, -a * _axisSign);
                Quaternion current = _hand.rotation;
                if (Quaternion.Angle(other, current) + 30f < Quaternion.Angle(same, current)) { _axisSign = -_axisSign; rot = other; }
                else rot = same;
            }
            else
            {
                rot = HandRotationFor(finger, palmNormal);
            }

            Vector3 fingers = rot * _fingerLocal;
            float bend = Vector3.Angle(fa, fingers);
            if (bend > maxWristBend && bend > 1e-3f)
                rot = Quaternion.FromToRotation(fingers, Vector3.Slerp(fa, fingers, maxWristBend / bend)) * rot;
            return rot;
        }

        /// <summary>The world rotation that puts the fingers along <paramref name="f"/> and the knuckles along <paramref name="k"/>.</summary>
        private Quaternion FrameRotation(Vector3 f, Vector3 k)
        {
            return Quaternion.LookRotation(f, k) * Quaternion.Inverse(Quaternion.LookRotation(_fingerLocal, _knuckleLocal));
        }

        private bool SolveWrist(Vector3 target, Vector3 poleHint, float weightTarget, float blendSpeed, float dt)
        {
            if (!Ready) return false;
            // A rebuilt body (appearance change) destroys these bones before the next capture runs.
            if (_upper == null || _fore == null || _hand == null) { Ready = false; return false; }
            _weight = Mathf.Lerp(_weight, weightTarget, 1f - Mathf.Exp(-blendSpeed * dt));
            if (_weight < 0.001f) return false;
            SolveBones(target, poleHint);
            return true;
        }

        /// <summary>Two-bone solve of the wrist onto <paramref name="target"/>, blended from the current pose by the eased weight.</summary>
        private void SolveBones(Vector3 target, Vector3 poleHint)
        {
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
