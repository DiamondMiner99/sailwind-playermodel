using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The handle layout of something turned by hand: a ship's wheel, a rope winch, an anchor windlass or a
    /// bilge pump. All of them are a rotor with handles sticking out of it, so one model covers them.
    ///
    /// Angles are in degrees in the rotor's own plane, 0 at local +Y. Wheels turn on local X, so positive runs
    /// toward local +Z; winches and pumps turn on local Z, so positive runs toward local +X. A handle is a point
    /// at (angle, radius, depth) in that frame. Because the point is fixed in the control's own transform, it
    /// turns exactly as the game turns the control, and a hand on it follows the real motion.
    /// </summary>
    internal sealed class RotorSpec
    {
        public bool IsWheel;
        public bool IsPump;
        public float[] Angles;
        public float TipRadius;      // local units, outermost point of the handles
        public float RimRadius;      // local units, inner edge of the handle region
        public float HandleDepth;    // local units along the axis, the plane the handles sit in
        public string Source;

        public Vector3 LocalAxis { get { return IsWheel ? Vector3.right : Vector3.forward; } }

        public Vector3 LocalPoint(float angleDeg, float radius, float depth)
        {
            float a = angleDeg * Mathf.Deg2Rad;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            return IsWheel ? new Vector3(depth, radius * c, radius * s) : new Vector3(radius * s, radius * c, depth);
        }

        /// <summary>Where on a handle the main hand holds, in local units.</summary>
        public float GripRadius
        {
            get
            {
                if (IsWheel) return RimRadius + (TipRadius - RimRadius) * 0.55f;
                if (IsPump) return TipRadius * 0.88f;
                return TipRadius * 0.8f;
            }
        }
    }

    internal static class RotorSpecs
    {
        private struct Row
        {
            public bool Wheel, Pump;
            public float[] A;
            public float Tip, Rim, Depth;
            public Row(bool wheel, bool pump, float tip, float rim, float depth, params float[] angles)
            { Wheel = wheel; Pump = pump; Tip = tip; Rim = rim; Depth = depth; A = angles; }
        }

        // Measured from every wheel, winch, windlass and pump mesh on the vanilla boats (level24 'the ocean').
        private static readonly Dictionary<string, Row> Table = new Dictionary<string, Row>(StringComparer.Ordinal)
        {
            { "steering_wheel",               new Row(true,  false, 0.606f, 0.412f, -0.183f, 0, 45, 90, 135, 180, 225, 270, 315) },
            { "steering_wheel_001",           new Row(true,  false, 0.666f, 0.453f, -0.201f, 0, 45, 90, 135, 180, 225, 270, 315) },
            { "steering_wheel_A",             new Row(true,  false, 0.566f, 0.364f, -0.183f, 0, 60, 120, 180, 240, 300) },
            { "anchor_winch",                 new Row(false, false, 1.156f, 0.636f,  0.489f, 2, 58, 118, 182, 238, 298) },
            { "paint_att_Winch_A_angle",      new Row(false, false, 0.198f, 0.110f,  0.117f, 5, 185) },
            { "paint_att_Winch_A_reef",       new Row(false, false, 0.241f, 0.160f,  0.128f, 1, 90, 181, 270) },
            { "paint_att_Winch_E_Anchor",     new Row(false, false, 0.268f, 0.122f,  0.124f, 27, 82, 139, 202, 259, 322) },
            { "paint_att_Winch_E__angle",     new Row(false, false, 0.171f, 0.104f,  0.118f, 69, 188, 309) },
            { "paint_att_Winch_E_reef",       new Row(false, false, 0.244f, 0.139f,  0.169f, 81, 200, 317) },
            { "paint_att_Winch_M_anchor",     new Row(false, false, 0.378f, 0.204f,  0.170f, 1, 61, 119, 181, 241, 299) },
            { "paint_att_Winch_M_angle",      new Row(false, false, 0.181f, 0.121f,  0.110f, 4, 184) },
            { "paint_att_Winch_M_reef",       new Row(false, false, 0.306f, 0.204f,  0.192f, 1, 90, 181, 270) },
            { "ropeController_back_left",     new Row(false, false, 0.183f, 0.109f,  0.110f, 2, 182) },
            { "ropeController_back_left_001", new Row(false, false, 0.183f, 0.109f,  0.110f, 2, 182) },
            { "ropeController_back_left_002", new Row(false, false, 0.183f, 0.109f,  0.110f, 2, 182) },
            { "ropeController_back_right",    new Row(false, false, 0.183f, 0.109f,  0.110f, 2, 182) },
            { "ropeController_middle_1",      new Row(false, false, 0.241f, 0.160f,  0.128f, 1, 90, 181, 270) },
            { "ropeController_middle_1_001",  new Row(false, false, 0.259f, 0.172f,  0.134f, 1, 90, 181, 270) },
            { "rope_winch_angle",             new Row(false, false, 0.198f, 0.110f,  0.117f, 3, 184) },
            { "rope_winch_angle_main",        new Row(false, false, 0.198f, 0.110f,  0.117f, 3, 184) },
            { "rope_winch_main1_angle",       new Row(false, false, 0.211f, 0.140f,  0.126f, 4, 184) },
            { "rope_winch_reef",              new Row(false, false, 0.241f, 0.160f,  0.128f, 1, 90, 181, 270) },
            { "rope_winch_reef_001",          new Row(false, false, 0.241f, 0.160f,  0.128f, 1, 90, 181, 270) },
            { "rope_winch_reef_002",          new Row(false, false, 0.241f, 0.160f,  0.128f, 1, 90, 181, 270) },
            { "winch_angle_back",             new Row(false, false, 0.202f, 0.120f,  0.121f, 2, 182) },
            { "Cylinder",                     new Row(false, true,  0.426f, 0.304f, -0.041f, 57, 177, 297) },
            { "Cylinder_007",                 new Row(false, true,  0.426f, 0.303f, -0.041f, 57, 177, 297) },
            { "pump_handle",                  new Row(false, true,  0.334f, 0.128f,  0.000f, 270) },
            { "pump_handle_002",              new Row(false, true,  0.480f, 0.332f, -0.147f, 273) },
        };

        private static readonly Dictionary<int, RotorSpec> _cache = new Dictionary<int, RotorSpec>();

        public static RotorSpec For(Transform control)
        {
            if (control == null) return null;
            int id = control.GetInstanceID();
            RotorSpec spec;
            if (_cache.TryGetValue(id, out spec)) return spec;

            bool wheel = control.GetComponent<GPButtonSteeringWheel>() != null;
            bool pump = control.GetComponent<BilgePump>() != null;
            var mf = control.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;

            Row row;
            if (mesh != null && Table.TryGetValue(mesh.name, out row) && row.Wheel == wheel && row.Pump == pump)
            {
                spec = new RotorSpec { IsWheel = wheel, IsPump = pump, Angles = row.A, TipRadius = row.Tip, RimRadius = row.Rim, HandleDepth = row.Depth, Source = "table" };
            }
            else
            {
                spec = FromMesh(mesh, wheel, pump) ?? FromBounds(mesh, wheel, pump);
            }
            _cache[id] = spec;
            Plugin.Log.LogInfo($"[Interaction] {(wheel ? "wheel" : pump ? "pump" : "winch")} '{control.name}' ({(mesh != null ? mesh.name : "no mesh")}): " +
                $"{spec.Angles.Length} handle(s), tip {spec.TipRadius:F3}, depth {spec.HandleDepth:F3}, from {spec.Source}");
            return spec;
        }

        /// <summary>
        /// Measure a control this mod has no table entry for, such as one on a modded boat. Same method as the
        /// offline measurement: the outermost vertices are the handles, grouped by angle; their median depth
        /// along the axis is the handle plane. Needs a readable mesh; returns null otherwise.
        /// </summary>
        private static RotorSpec FromMesh(Mesh mesh, bool wheel, bool pump)
        {
            if (mesh == null || !mesh.isReadable) return null;
            Vector3[] v;
            try { v = mesh.vertices; } catch { return null; }
            if (v == null || v.Length < 8) return null;

            float rmax = 0f;
            var r = new float[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                float a = v[i].y, b = wheel ? v[i].z : v[i].x;
                r[i] = Mathf.Sqrt(a * a + b * b);
                if (r[i] > rmax) rmax = r[i];
            }
            if (rmax < 1e-3f) return null;

            const int Bins = 24;
            var count = new int[Bins];
            var sumSin = new float[Bins];
            var sumCos = new float[Bins];
            var depths = new List<float>();
            var inner = new List<float>();
            for (int i = 0; i < v.Length; i++)
            {
                if (r[i] < 0.82f * rmax) { inner.Add(r[i]); continue; }
                float b = wheel ? v[i].z : v[i].x;
                float ang = Mathf.Atan2(b, v[i].y);
                int bin = (int)(((ang * Mathf.Rad2Deg) + 360f) % 360f / (360f / Bins)) % Bins;
                count[bin]++; sumSin[bin] += Mathf.Sin(ang); sumCos[bin] += Mathf.Cos(ang);
                depths.Add(wheel ? v[i].x : v[i].z);
            }

            var angles = new List<float>();
            for (int i = 0; i < Bins; i++)
            {
                if (count[i] == 0) continue;
                int prev = count[(i + Bins - 1) % Bins], next = count[(i + 1) % Bins];
                if (count[i] < prev || count[i] < next) continue;
                if (count[i] == prev && prev > 0) continue; // plateau: keep only its first bin
                float s = sumSin[i] + sumSin[(i + 1) % Bins] + sumSin[(i + Bins - 1) % Bins];
                float c = sumCos[i] + sumCos[(i + 1) % Bins] + sumCos[(i + Bins - 1) % Bins];
                angles.Add((Mathf.Atan2(s, c) * Mathf.Rad2Deg + 360f) % 360f);
            }
            if (angles.Count == 0) return null;
            angles.Sort();
            for (int i = angles.Count - 1; i > 0; i--)
                if (angles[i] - angles[i - 1] < 20f) angles.RemoveAt(i);
            if (angles.Count > 1 && angles[0] + 360f - angles[angles.Count - 1] < 20f) angles.RemoveAt(angles.Count - 1);

            depths.Sort();
            inner.Sort();
            float depth = depths[depths.Count / 2];
            float rim = inner.Count > 0 ? inner[(int)(inner.Count * 0.9f)] : rmax * 0.6f;
            return new RotorSpec { IsWheel = wheel, IsPump = pump, Angles = angles.ToArray(), TipRadius = rmax, RimRadius = rim, HandleDepth = depth, Source = "mesh" };
        }

        /// <summary>Last resort from the mesh bounds: a wheel of eight handles, or a two-handled crank.</summary>
        private static RotorSpec FromBounds(Mesh mesh, bool wheel, bool pump)
        {
            Bounds b = mesh != null ? mesh.bounds : new Bounds(Vector3.zero, wheel ? new Vector3(0.4f, 1.1f, 1.1f) : new Vector3(0.4f, 0.4f, 0.3f));
            Vector3 c = b.center, e = b.extents;
            if (wheel)
            {
                float tip = Mathf.Max(e.y, e.z);
                float depth = Mathf.Abs(b.min.x) > Mathf.Abs(b.max.x) ? b.min.x * 0.85f : b.max.x * 0.85f;
                return new RotorSpec { IsWheel = true, Angles = new float[] { 0, 45, 90, 135, 180, 225, 270, 315 }, TipRadius = tip, RimRadius = tip * 0.68f, HandleDepth = depth, Source = "bounds" };
            }
            float tipC = Mathf.Max(Mathf.Abs(c.x) + e.x, Mathf.Abs(c.y) + e.y);
            float depthC = Mathf.Abs(b.max.z) >= Mathf.Abs(b.min.z) ? b.max.z * 0.85f : b.min.z * 0.85f;
            return new RotorSpec { IsWheel = false, IsPump = pump, Angles = pump ? new float[] { 270 } : new float[] { 0, 180 }, TipRadius = tipC, RimRadius = tipC * 0.55f, HandleDepth = depthC, Source = "bounds" };
        }
    }

    /// <summary>
    /// Both hands on a rotor, for one body.
    ///
    /// Small cranks (winches, pumps): both hands on ONE handle, locked to it for as long as it is held, so the
    /// arms go round together and can never cross.
    ///
    /// Large rotors (a wheel, a big windlass): worked hand over hand. On a wheel each hand keeps to its own
    /// half, right hand on the right, and lets go to take another handle when its handle turns too far from
    /// where the hand wants to be or crosses the top or bottom into the other hand's half. A windlass with bars
    /// too far apart for one hand each is pushed with both hands on one bar, taking the next bar in turn.
    /// </summary>
    internal sealed class RotorGrip
    {
        private const float RegripSeconds = 0.22f;
        private const float MidlineMargin = 8f;

        private Transform _control;
        private RotorSpec _spec;
        private int _handleR = -1, _handleL = -1;
        private float _regripR, _regripL;
        private Vector3 _fromLocalR, _fromLocalL;     // last frame's palm, control-local
        private Vector3 _startLocalR, _startLocalL;   // where a re-grip let go, control-local
        private bool _continuous, _sameHandle;
        private bool _logged;

        public bool Active { get { return _control != null && _spec != null; } }
        public Transform Control { get { return _control; } }

        // Per-frame results.
        public Vector3 Hub, FaceNormal, StandPoint, PalmR, PalmL, FingerR, FingerL, NormalR, NormalL, AxisR, AxisL;
        public float GripRadiusWorld;

        public void Release()
        {
            _control = null;
            _spec = null;
            _handleR = _handleL = -1;
        }

        /// <summary>
        /// Update the grips. <paramref name="bodyPos"/> is the vanilla player position, never the moved body;
        /// <paramref name="shoulderForward"/> is how far the shoulders sit in front of it, so the stand distance
        /// is measured from the shoulders.
        /// </summary>
        public bool Update(Transform control, Vector3 bodyPos, float shoulderForward, float armLength, float dt)
        {
            if (control == null) { Release(); return false; }
            if (control != _control)
            {
                _control = control;
                _spec = RotorSpecs.For(control);
                _handleR = _handleL = -1;
                _regripR = _regripL = 0f;
                _logged = false;
            }
            if (_spec == null || _spec.Angles == null || _spec.Angles.Length == 0) return false;

            // Axis and handle face from world POINTS, not TransformDirection, so a mirrored transform cannot
            // put the player on the wrong side.
            Vector3 origin = control.TransformPoint(Vector3.zero);
            Vector3 axis = (control.TransformPoint(_spec.LocalAxis) - origin).normalized;
            float scale = (control.TransformPoint(Vector3.up) - origin).magnitude;
            Hub = control.TransformPoint(_spec.LocalPoint(0f, 0f, _spec.HandleDepth));
            GripRadiusWorld = _spec.GripRadius * scale;

            Vector3 toHandles = Hub - origin;
            Vector3 face = toHandles.magnitude > 0.02f ? toHandles.normalized
                         : axis * (Vector3.Dot(bodyPos - Hub, axis) >= 0f ? 1f : -1f);
            FaceNormal = face;

            // "Up" in the rotor plane. A winch lying flat (axis near vertical) has no up, so its near side is.
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (up.sqrMagnitude < 0.06f) up = Vector3.ProjectOnPlane(bodyPos - Hub, axis);
            if (up.sqrMagnitude < 1e-4f) up = control.TransformDirection(Vector3.up);
            up.Normalize();
            // "Right" is the right of a person facing the hub, laid into the rotor plane, so left and right
            // follow where the player stands and not how the mesh was built.
            Vector3 toHub = Hub - bodyPos; toHub.y = 0f;
            Vector3 personRight = toHub.sqrMagnitude > 1e-4f ? Vector3.Cross(Vector3.up, toHub.normalized) : Vector3.Cross(up, -face);
            Vector3 right = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(personRight, axis), up);
            if (right.sqrMagnitude < 0.04f) right = Vector3.Cross(up, -face);
            right.Normalize();

            int n = _spec.Angles.Length;
            float arc = n > 1 ? 2f * Mathf.PI * GripRadiusWorld / n : 99f;
            _continuous = !_spec.IsWheel && (_spec.IsPump || GripRadiusWorld <= 0.45f);
            _sameHandle = n == 1 || _continuous || arc > 0.7f;

            float idealR, idealL;
            if (_sameHandle) { idealR = idealL = _continuous ? InteractionTuning.CrankHandAngle.Value : 80f; }
            else
            {
                float a = _spec.IsWheel ? InteractionTuning.HelmHandAngle.Value : InteractionTuning.CrankHandAngle.Value;
                idealR = a; idealL = -a;
            }

            if (_handleR < 0)
            {
                _handleR = Pick(control, up, right, idealR, -1, _sameHandle ? 0 : 1);
                _handleL = _sameHandle ? _handleR : Pick(control, up, right, idealL, _handleR, -1);
            }
            else if (!_continuous)
            {
                float release = InteractionTuning.HelmReleaseArc.Value;
                if (_sameHandle)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(WorldAngle(control, _handleR, up, right), idealR)) > release)
                    {
                        int next = Pick(control, up, right, idealR, _handleR, 0);
                        if (next != _handleR) { StartRegrip(true, next); StartRegrip(false, next); }
                    }
                }
                else
                {
                    float angR = WorldAngle(control, _handleR, up, right);
                    if (!InZone(angR, 1) || Mathf.Abs(Mathf.DeltaAngle(angR, idealR)) > release)
                    {
                        int next = Pick(control, up, right, idealR, _handleL, 1);
                        if (next != _handleR) StartRegrip(true, next);
                    }
                    float angL = WorldAngle(control, _handleL, up, right);
                    if (!InZone(angL, -1) || Mathf.Abs(Mathf.DeltaAngle(angL, idealL)) > release)
                    {
                        int next = Pick(control, up, right, idealL, _handleR, -1);
                        if (next != _handleL) StartRegrip(false, next);
                    }
                }
            }

            float gripR = _spec.GripRadius;
            // Two hands on one handle sit one beside the other along it, the outer hand at the grip.
            float gripL = _sameHandle ? Mathf.Max(_spec.RimRadius * 0.9f, gripR - 0.11f / Mathf.Max(scale, 1e-3f)) : gripR;
            Vector3 targetR = control.TransformPoint(_spec.LocalPoint(_spec.Angles[_handleR], gripR, _spec.HandleDepth));
            Vector3 targetL = control.TransformPoint(_spec.LocalPoint(_spec.Angles[_handleL], gripL, _spec.HandleDepth));

            PalmR = Regrip(ref _regripR, _startLocalR, targetR, face, dt);
            PalmL = Regrip(ref _regripL, _startLocalL, targetL, face, dt);
            _fromLocalR = control.InverseTransformPoint(PalmR);
            _fromLocalL = control.InverseTransformPoint(PalmL);

            // Each handle runs out from the hub, and the fist closes round it.
            AxisR = Radial(control, _handleR, axis);
            AxisL = Radial(control, _handleL, axis);
            NormalR = NormalL = -face;
            FingerR = Vector3.Cross(axis, AxisR);
            FingerL = Vector3.Cross(axis, AxisL);

            // Stand in front of the handle face, a comfortable arm's length back from the shoulders, shifted
            // toward where the hands want to be so a windlass bar on one side is in reach.
            Vector3 flatFace = new Vector3(face.x, 0f, face.z);
            if (flatFace.sqrMagnitude < 0.12f) { flatFace = bodyPos - Hub; flatFace.y = 0f; }
            if (flatFace.sqrMagnitude < 1e-4f) flatFace = Vector3.forward;
            flatFace.Normalize();
            Vector3 idealMid = (Rim(up, right, GripRadiusWorld, idealR) + Rim(up, right, GripRadiusWorld, idealL)) * 0.5f;
            Vector3 lateral = idealMid; lateral.y = 0f;
            lateral -= Vector3.Dot(lateral, flatFace) * flatFace;
            float stand = armLength * InteractionTuning.StandFraction.Value + shoulderForward;
            StandPoint = Hub + flatFace * stand + lateral * 0.7f;

            if (!_logged)
            {
                _logged = true;
                Vector3 fromHub = bodyPos - Hub; fromHub.y = 0f;
                float along = Vector3.Dot(fromHub, flatFace);
                Vector3 move = StandPoint - bodyPos; move.y = 0f;
                Plugin.Log.LogInfo($"[Interaction] took hold of '{control.name}': player {along:F2} m in front of the handles " +
                    $"({fromHub.magnitude:F2} m from the hub), standing {stand:F2} m back, moving {move.magnitude:F2} m " +
                    $"({(along > stand ? "closer" : "back")}); grip radius {GripRadiusWorld:F2}, " +
                    $"{(_continuous ? "crank, one handle" : _sameHandle ? "hand over hand on one bar" : "hand over hand")}");
            }
            return true;
        }

        private static bool InZone(float angle, int side)
        {
            if (side > 0) return angle > MidlineMargin && angle < 180f - MidlineMargin;
            if (side < 0) return angle < -MidlineMargin && angle > -180f + MidlineMargin;
            return true;
        }

        private void StartRegrip(bool rightHand, int handle)
        {
            if (rightHand) { _handleR = handle; _regripR = RegripSeconds; _startLocalR = _fromLocalR; }
            else { _handleL = handle; _regripL = RegripSeconds; _startLocalL = _fromLocalL; }
        }

        private Vector3 Regrip(ref float timer, Vector3 fromLocal, Vector3 target, Vector3 face, float dt)
        {
            if (timer <= 0f) return target;
            timer = Mathf.Max(0f, timer - dt);
            float u = 1f - timer / RegripSeconds;
            Vector3 from = _control.TransformPoint(fromLocal);
            // Lift the hand off the handle toward the player on the way across.
            return Vector3.Lerp(from, target, Mathf.SmoothStep(0f, 1f, u)) + face * (0.09f * Mathf.Sin(u * Mathf.PI));
        }

        /// <summary>
        /// The handle nearest <paramref name="ideal"/>, preferring handles on the given side (1 right, -1 left,
        /// 0 either). Falls back to any handle when none is on that side, as a two-handled bar is when it stands
        /// straight up and down.
        /// </summary>
        private int Pick(Transform control, Vector3 up, Vector3 right, float ideal, int exclude, int side)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int pass = 0; pass < 2 && best < 0; pass++)
            {
                for (int k = 0; k < _spec.Angles.Length; k++)
                {
                    if (k == exclude && _spec.Angles.Length > 1) continue;
                    float ang = WorldAngle(control, k, up, right);
                    if (pass == 0 && !InZone(ang, side)) continue;
                    float d = Mathf.Abs(Mathf.DeltaAngle(ang, ideal));
                    if (d < bestD) { bestD = d; best = k; }
                }
            }
            return best < 0 ? 0 : best;
        }

        private float WorldAngle(Transform control, int k, Vector3 up, Vector3 right)
        {
            Vector3 d = control.TransformPoint(_spec.LocalPoint(_spec.Angles[k], _spec.TipRadius, _spec.HandleDepth)) - Hub;
            return Mathf.Atan2(Vector3.Dot(d, right), Vector3.Dot(d, up)) * Mathf.Rad2Deg;
        }

        private Vector3 Radial(Transform control, int k, Vector3 axis)
        {
            Vector3 d = control.TransformPoint(_spec.LocalPoint(_spec.Angles[k], _spec.TipRadius, _spec.HandleDepth)) - Hub;
            d = Vector3.ProjectOnPlane(d, axis);
            return d.sqrMagnitude > 1e-8f ? d.normalized : control.TransformDirection(Vector3.up);
        }

        private static Vector3 Rim(Vector3 up, Vector3 right, float radius, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad;
            return radius * (Mathf.Cos(a) * up + Mathf.Sin(a) * right);
        }
    }
}
