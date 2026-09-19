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
        // A tiller: the game's steering wheel put on a tiller arm by a mod (Shipyard Expansion's tiller option, and
        // possibly others'). The wheel always turns on its local X, so a tiller is one whose X stands upright and swings
        // the arm side to side. It is held at the end of the arm rather than round a rim.
        public bool IsTiller;
        public Vector3 TillerTipLocal, TillerGripLocal;
        public string TillerSource;   // "mesh", "bounds" or "no mesh": where the tiller's end was measured from
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
            Bounds tillerBounds = default(Bounds);
            if (wheel && LooksLikeTiller(control, mesh)) tillerBounds = MeasureTiller(spec, control, mesh, Vector3.up);
            _cache[id] = spec;
            if (spec.IsTiller)
            {
                Vector3 g = spec.TillerGripLocal, t = spec.TillerTipLocal;
                Plugin.Log.LogInfo($"[Interaction] tiller '{control.name}' ({(mesh != null ? mesh.name : "no mesh")}): " +
                    $"held at ({g.x:F3}, {g.y:F3}, {g.z:F3}), end at ({t.x:F3}, {t.y:F3}, {t.z:F3}), from {spec.TillerSource}" +
                    (spec.TillerSource == "bounds" ? $", bounds x {tillerBounds.min.x:F3} to {tillerBounds.max.x:F3}" : ""));
            }
            else
                Plugin.Log.LogInfo($"[Interaction] {(wheel ? "wheel" : pump ? "pump" : "winch")} '{control.name}' ({(mesh != null ? mesh.name : "no mesh")}): " +
                    $"{spec.Angles.Length} handle(s), tip {spec.TipRadius:F3}, depth {spec.HandleDepth:F3}, from {spec.Source}");
            return spec;
        }

        /// <summary>True for a steering control that is a tiller, not a wheel. Safe to call on anything.</summary>
        public static bool IsTiller(Transform control)
        {
            if (control == null || control.GetComponent<GPButtonSteeringWheel>() == null) return false;
            var spec = For(control);
            return spec != null && spec.IsTiller;
        }

        /// <summary>
        /// A steering wheel turning about an axis that stands roughly upright swings side to side, which is a tiller.
        /// Named "tiller" counts too, for one built on a heeled or oddly turned parent.
        /// </summary>
        private static bool LooksLikeTiller(Transform control, Mesh mesh)
        {
            Vector3 axis = control.TransformPoint(Vector3.right) - control.position;
            if (axis.sqrMagnitude > 1e-8f && Mathf.Abs(Vector3.Dot(axis.normalized, Vector3.up)) > 0.6f) return true;
            return control.name.IndexOf("tiller", StringComparison.OrdinalIgnoreCase) >= 0
                   || (mesh != null && mesh.name.IndexOf("tiller", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Where a tiller's arm ends, in the control's own units. Across the turning axis it is the point of the mesh
        /// farthest from the pivot, held a little short of the very end. Along the axis (the arm's height) it is the
        /// middle of the arm near its end when the mesh can be read. Otherwise it is a little under the face of the
        /// bounds on the upward side: the bounds also take in the rudder stock below the pivot, so their middle sits
        /// well under the arm. Returns the bounds, for the log.
        /// </summary>
        private static Bounds MeasureTiller(RotorSpec spec, Transform control, Mesh mesh, Vector3 up)
        {
            // From world points, like RotorGrip, so a mirrored transform keeps its sign.
            Vector3 xw = control.TransformPoint(Vector3.right) - control.position;
            float scale = Mathf.Max(xw.magnitude, 1e-3f);
            float upSign = Vector3.Dot(xw, up) >= 0f ? 1f : -1f;

            Vector3 tip = Vector3.zero;
            string source = null;
            if (mesh != null && mesh.isReadable)
            {
                try
                {
                    Vector3[] verts = mesh.vertices;
                    float best = 0f;
                    foreach (var v in verts)
                    {
                        float r = v.y * v.y + v.z * v.z;
                        if (r > best) { best = r; tip = v; }
                    }
                    if (best > 1e-8f)
                    {
                        // The arm's height near its end: halfway between the lowest and highest vertex a little short
                        // of the tip, in the tip's direction. Min and max, not a mean, so a densely modeled bevel
                        // cannot pull it.
                        float radius = Mathf.Sqrt(best);
                        float uy = tip.y / radius, uz = tip.z / radius;
                        float lo = float.MaxValue, hi = float.MinValue;
                        foreach (var v in verts)
                        {
                            float r = Mathf.Sqrt(v.y * v.y + v.z * v.z);
                            if (r < 0.75f * radius || r > 0.95f * radius) continue;
                            if ((v.y * uy + v.z * uz) / r < 0.866f) continue;
                            if (v.x < lo) lo = v.x;
                            if (v.x > hi) hi = v.x;
                        }
                        tip = new Vector3(lo <= hi ? (lo + hi) * 0.5f : tip.x, uy * radius, uz * radius);
                        source = "mesh";
                    }
                }
                catch { source = null; }
            }

            Bounds b = mesh != null ? mesh.bounds : new Bounds(new Vector3(0f, 0f, 0.5f), new Vector3(0.1f, 0.1f, 1f));
            if (source == null)
            {
                // The side of the bounds that reaches farthest out across the axis.
                Vector3[] ends = { new Vector3(b.center.x, b.max.y, b.center.z), new Vector3(b.center.x, b.min.y, b.center.z),
                                   new Vector3(b.center.x, b.center.y, b.max.z), new Vector3(b.center.x, b.center.y, b.min.z) };
                float best = -1f;
                foreach (var e in ends)
                {
                    float r = e.y * e.y + e.z * e.z;
                    if (r > best) { best = r; tip = e; }
                }
                // Level with the arm: 3.5 cm under the upward face, about half an arm's thickness.
                tip.x = (upSign > 0f ? b.max.x : b.min.x) - upSign * 0.035f / scale;
                source = mesh != null ? "bounds" : "no mesh";
            }
            spec.IsTiller = true;
            spec.TillerTipLocal = tip;
            spec.TillerGripLocal = new Vector3(tip.x, tip.y * 0.85f, tip.z * 0.85f);
            spec.TillerSource = source;
            return b;
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

        /// <summary>
        /// Where a rotor's handles are this frame, as seen by one body: the handle plane, which way it faces,
        /// which way is up and right in it, and where each hand wants to be on it. Built once by
        /// <see cref="BuildFrame"/> so the standing pose and the seated reach measurement read the same geometry
        /// and cannot drift apart.
        /// </summary>
        private struct RotorFrame
        {
            public Vector3 Origin, Axis, Hub, Face, Up, Right;
            public float Scale;
            public float GripRadiusWorld;
            public bool Continuous;   // the hands never let go: a pump or a small winch, turned all the way round
            public bool SameHandle;   // both hands on one handle
            public float IdealR, IdealL;   // degrees in the rotor plane, where each hand wants to be
            public float GripR, GripL;     // local units, how far out along its handle each hand holds
        }

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
            RotorFrame f;
            if (!BuildFrame(control, _spec, bodyPos, out f)) return false;

            Vector3 axis = f.Axis, face = f.Face, up = f.Up, right = f.Right;
            Hub = f.Hub;
            FaceNormal = face;
            GripRadiusWorld = f.GripRadiusWorld;
            _continuous = f.Continuous;
            _sameHandle = f.SameHandle;
            float idealR = f.IdealR, idealL = f.IdealL;

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

            float gripR = f.GripR;
            float gripL = f.GripL;
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

        /// <summary>
        /// The rotor's frame as seen from <paramref name="bodyPos"/>. False for anything with no handles to hold.
        /// </summary>
        private static bool BuildFrame(Transform control, RotorSpec spec, Vector3 bodyPos, out RotorFrame f)
        {
            f = default(RotorFrame);
            if (control == null || spec == null || spec.Angles == null || spec.Angles.Length == 0) return false;

            // Axis and handle face from world POINTS, not TransformDirection, so a mirrored transform cannot
            // put the player on the wrong side.
            f.Origin = control.TransformPoint(Vector3.zero);
            f.Axis = (control.TransformPoint(spec.LocalAxis) - f.Origin).normalized;
            f.Scale = (control.TransformPoint(Vector3.up) - f.Origin).magnitude;
            f.Hub = control.TransformPoint(spec.LocalPoint(0f, 0f, spec.HandleDepth));
            f.GripRadiusWorld = spec.GripRadius * f.Scale;

            Vector3 toHandles = f.Hub - f.Origin;
            f.Face = toHandles.magnitude > 0.02f ? toHandles.normalized
                   : f.Axis * (Vector3.Dot(bodyPos - f.Hub, f.Axis) >= 0f ? 1f : -1f);

            // "Up" in the rotor plane. A winch lying flat (axis near vertical) has no up, so its near side is.
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, f.Axis);
            if (up.sqrMagnitude < 0.06f) up = Vector3.ProjectOnPlane(bodyPos - f.Hub, f.Axis);
            if (up.sqrMagnitude < 1e-4f) up = control.TransformDirection(Vector3.up);
            up.Normalize();
            f.Up = up;
            // "Right" is the right of a person facing the hub, laid into the rotor plane, so left and right
            // follow where the player stands and not how the mesh was built.
            Vector3 toHub = f.Hub - bodyPos; toHub.y = 0f;
            Vector3 personRight = toHub.sqrMagnitude > 1e-4f ? Vector3.Cross(Vector3.up, toHub.normalized) : Vector3.Cross(up, -f.Face);
            Vector3 right = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(personRight, f.Axis), up);
            if (right.sqrMagnitude < 0.04f) right = Vector3.Cross(up, -f.Face);
            right.Normalize();
            f.Right = right;

            int n = spec.Angles.Length;
            float arc = n > 1 ? 2f * Mathf.PI * f.GripRadiusWorld / n : 99f;
            f.Continuous = !spec.IsWheel && (spec.IsPump || f.GripRadiusWorld <= 0.45f);
            f.SameHandle = n == 1 || f.Continuous || arc > 0.7f;

            if (f.SameHandle)
            {
                f.IdealR = f.IdealL = f.Continuous ? InteractionTuning.CrankHandAngle.Value : 80f;
            }
            else
            {
                float a = spec.IsWheel ? InteractionTuning.HelmHandAngle.Value : InteractionTuning.CrankHandAngle.Value;
                f.IdealR = a; f.IdealL = -a;
            }

            f.GripR = spec.GripRadius;
            // Two hands on one handle sit one beside the other along it, the outer hand at the grip.
            f.GripL = f.SameHandle ? Mathf.Max(spec.RimRadius * 0.9f, f.GripR - 0.11f / Mathf.Max(f.Scale, 1e-3f)) : f.GripR;
            return true;
        }

        /// <summary>
        /// How far a seated body would have to reach to work this control, and the point between its two hands.
        /// <paramref name="need"/> is the larger of the two hands' distances from its own shoulder to the farthest
        /// point on the handles that hand is carried to before it lets go, so a wheel put hard over and a pump
        /// turned all the way round are measured where the arms are longest; <paramref name="aim"/> is the midpoint
        /// of the two, for the lean and the behind-the-seat test. Measured through the same frame the standing pose
        /// is built from, so the two can never disagree. False for a tiller and for anything with no handles to hold.
        /// </summary>
        public static bool MeasureSeated(Transform control, Vector3 shoulderMid, Vector3 shoulderRight, float shoulderHalf,
            out float need, out Vector3 aim)
        {
            need = 0f;
            aim = shoulderMid;
            if (control == null) return false;
            var spec = RotorSpecs.For(control);
            if (spec == null || spec.IsTiller) return false;
            RotorFrame f;
            if (!BuildFrame(control, spec, shoulderMid, out f)) return false;

            Vector3 handR = shoulderMid + shoulderRight * shoulderHalf;
            Vector3 handL = shoulderMid - shoulderRight * shoulderHalf;
            Vector3 wantR, wantL;
            if (f.Continuous)
            {
                // The hands never let go of a pump or a small winch, so each one really does visit the far side of
                // its own sweep circle every stroke. Measure to there, not to where it first takes hold.
                wantR = FarthestOnSweep(f, f.GripR * f.Scale, handR);
                wantL = FarthestOnSweep(f, f.GripL * f.Scale, handL);
            }
            else
            {
                // A hand keeps its handle until it has turned HelmReleaseArc past where it wants to be, or out of
                // its own half of the rim, so a wheel put hard over carries it well away from the angle it took
                // hold at. Measure to the farthest that carries it, not to where it first takes hold.
                float release = InteractionTuning.HelmReleaseArc.Value;
                float loR, hiR, loL, hiL;
                HandArc(f.IdealR, release, f.SameHandle ? 0 : 1, out loR, out hiR);
                HandArc(f.IdealL, release, f.SameHandle ? 0 : -1, out loL, out hiL);
                wantR = FarthestOnArc(f, f.GripR * f.Scale, handR, loR, hiR);
                wantL = FarthestOnArc(f, f.GripL * f.Scale, handL, loL, hiL);
            }
            need = Mathf.Max(Vector3.Distance(handR, wantR), Vector3.Distance(handL, wantL));
            aim = (wantR + wantL) * 0.5f;
            return true;
        }

        /// <summary>
        /// How far round its handle can carry one hand before it lets go: the release arc either side of where the
        /// hand wants to be, kept inside that hand's half of the rim (side 1 right, -1 left, 0 either).
        /// </summary>
        private static void HandArc(float ideal, float release, int side, out float lo, out float hi)
        {
            lo = ideal - release;
            hi = ideal + release;
            if (side > 0) { lo = Mathf.Max(lo, MidlineMargin); hi = Mathf.Min(hi, 180f - MidlineMargin); }
            else if (side < 0) { lo = Mathf.Max(lo, -180f + MidlineMargin); hi = Mathf.Min(hi, -MidlineMargin); }
        }

        /// <summary>
        /// The point between <paramref name="lo"/> and <paramref name="hi"/> degrees on a sweep circle of the given
        /// world radius that is farthest from <paramref name="from"/>. The distance to a circle climbs steadily to
        /// either side of the near point, so the farthest is at one end of the arc, or at the far point of the whole
        /// circle when that falls inside it.
        /// </summary>
        private static Vector3 FarthestOnArc(RotorFrame f, float radius, Vector3 from, float lo, float hi)
        {
            Vector3 best = f.Hub + Rim(f.Up, f.Right, radius, lo);
            Vector3 end = f.Hub + Rim(f.Up, f.Right, radius, hi);
            if ((end - from).sqrMagnitude > (best - from).sqrMagnitude) best = end;
            Vector3 far = FarthestOnSweep(f, radius, from);
            Vector3 d = far - f.Hub;
            float ang = Mathf.Atan2(Vector3.Dot(d, f.Right), Vector3.Dot(d, f.Up)) * Mathf.Rad2Deg;
            // The arc can run past a half turn, so ask how far into it that angle lies rather than comparing signs.
            if (Mathf.Repeat(ang - lo, 360f) <= hi - lo && (far - from).sqrMagnitude > (best - from).sqrMagnitude) best = far;
            return best;
        }

        /// <summary>The point of a sweep circle of the given world radius that is farthest from <paramref name="from"/>.</summary>
        private static Vector3 FarthestOnSweep(RotorFrame f, float radius, Vector3 from)
        {
            Vector3 d = from - f.Hub;
            Vector3 inPlane = Vector3.Dot(d, f.Up) * f.Up + Vector3.Dot(d, f.Right) * f.Right;
            if (inPlane.sqrMagnitude < 1e-6f) return f.Hub + f.Up * radius;
            return f.Hub - inPlane.normalized * radius;
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

    /// <summary>
    /// One hand on a tiller, for one body.
    ///
    /// The hand is picked when the tiller is taken, from the side the body is on, and changes only if the body ends
    /// up well over on the other side. Standing, the body stays where it is beside the end, moving only as far as it
    /// must when the swinging arm would reach its hips or the end would leave its reach; <see cref="Forward"/> is the
    /// way the tiller points, for the body to face. Seated, the seat places the body and only the hand is decided here.
    ///
    /// Where the body stands is measured from where the end rests with the rudder amidships, which is fixed to the
    /// boat, so steering does not drag the body about with the end.
    /// </summary>
    internal sealed class TillerGrip
    {
        private const float HipClear = 0.22f;         // meters across from the arm (pivot to tip) to the body's centerline
        private const float HipDepth = 0.10f;         // meters from the body's centerline to its front and back, where the arm is measured
        private const float ReachSlack = 0.08f;       // meters past the arm's flat reach before the body follows
        private const float ForeBack = 0.30f;         // how far behind the end the body may stand
        private const float ForeAhead = 0.10f;        // how far ahead of it
        private const float BackSwungAway = 0.03f;    // how far behind the end once the tiller has swung away from the body
        private const float SwungAwayBlend = 0.30f;   // meters of swing away from the body over which ForeBack narrows to that
        private const float LatchStanding = 0.25f;    // meters over on the other side before the hand changes
        private const float LatchSeated = 0.12f;
        private const float PickDeadband = 0.02f;
        private const float SettleSeconds = 0.4f;     // a crewmate's position is still catching up: re-pick until then

        private Transform _control;
        private RotorSpec _spec;
        private int _side;            // 0 unset; -1 the right hand (standing on the tiller's -Right side); 1 the left hand
        private bool _seated;
        private float _held;
        private float _bx, _by;       // where the body stands: across from the rest point (toward its side) and along Forward
        private bool _logged;
        private Vector3 _restGrip;
        // The arm in the rest frame, like _bx and _by: along and across (toward the body's side) at the pivot and the tip,
        // and the farther across of the grip and the tip. Set by the standing update.
        private float _pivotF, _pivotX, _tipF, _tipX, _endSide;

        // Per-frame results.
        public Vector3 Grip, Tip, Along, Forward, Right, StandPoint, FingerHint;
        public float MinSide;

        public bool RightHand { get { return _side <= 0; } }
        public bool Active { get { return _control != null && _spec != null && _spec.IsTiller; } }
        /// <summary>From the tiller toward the side the body stands on.</summary>
        public Vector3 AwaySide { get { return Right * _side; } }

        /// <summary>How far <paramref name="p"/> would have to move away from the tiller to clear the swinging arm.</summary>
        public float ClearanceGap(Vector3 p)
        {
            float f = Vector3.Dot(p - _restGrip, Forward);
            return Mathf.Max(_endSide, ArmSide(f)) + HipClear - Vector3.Dot(p - _restGrip, Right) * _side;
        }

        /// <summary>
        /// How far across toward the body's side the arm reaches beside a body centered <paramref name="f"/> meters along
        /// the rest line, from its back to its front. Swung away from the body, the arm runs back from the end toward the
        /// body's side, so beside a body standing behind the end it is nearer than the end is.
        /// </summary>
        private float ArmSide(float f)
        {
            // Along the arm the across position only ever changes one way, so the nearest is at the back or the front.
            return Mathf.Max(ArmAcross(f - HipDepth), ArmAcross(f + HipDepth));
        }

        private float ArmAcross(float f)
        {
            float span = _tipF - _pivotF;
            if (Mathf.Abs(span) < 1e-3f) return Mathf.Max(_pivotX, _tipX);
            return Mathf.Lerp(_pivotX, _tipX, (f - _pivotF) / span);   // clamped: the pivot's or the tip's beyond either end
        }

        public void Release()
        {
            _control = null;
            _spec = null;
            _side = 0;
            _held = 0f;
            _logged = false;
        }

        /// <summary>
        /// Update the hold. False for anything that is not a tiller. <paramref name="bodyPos"/> is the player's own
        /// position, never the moved body; <paramref name="seatHips"/> and <paramref name="seatRight"/> are read only
        /// while <paramref name="seated"/>; <paramref name="shoulderY"/> is the world height of the shoulders.
        /// </summary>
        public bool Update(Transform control, float dt, Vector3 bodyPos, bool seated, Vector3 seatHips, Vector3 seatRight,
            float armLength, float shoulderY, float shoulderHalf)
        {
            if (control == null) { Release(); return false; }
            if (control != _control)
            {
                _control = control;
                _spec = RotorSpecs.For(control);
                _side = 0;
                _held = 0f;
                _logged = false;
            }
            // Every frame, so a wheel held after a tiller never reports a tiller.
            if (_spec == null || !_spec.IsTiller) return false;
            if (seated != _seated) { _seated = seated; _side = 0; _held = 0f; _logged = false; }
            _held += dt;

            Vector3 pivot = control.position;
            Grip = control.TransformPoint(_spec.TillerGripLocal);
            Tip = control.TransformPoint(_spec.TillerTipLocal);
            Along = (Tip - Grip).sqrMagnitude > 1e-6f ? (Tip - Grip).normalized : (Tip - pivot).normalized;

            // The end at rest. The game turns the control only through localEulerAngles = (angle, 0, 0)
            // (GPButtonSteeringWheel.ApplyWheelRotationFromRudder), so amidships is its local rotation at identity.
            _restGrip = control.parent != null
                ? control.parent.TransformPoint(control.localPosition + Vector3.Scale(control.localScale, _spec.TillerGripLocal))
                : Grip;
            Forward = _restGrip - pivot;
            Forward.y = 0f;
            if (Forward.sqrMagnitude < 1e-6f) { Forward = Along; Forward.y = 0f; }
            if (Forward.sqrMagnitude < 1e-6f) Forward = Vector3.forward;
            Forward.Normalize();
            Right = Vector3.Cross(Vector3.up, Forward);

            // Which side, and so which hand. Seated, from where the seat is; standing, from where the player is.
            float d = seated ? -Vector3.Dot(_restGrip - seatHips, seatRight) : Vector3.Dot(bodyPos - _restGrip, Right);
            bool settling = _held < SettleSeconds;
            bool seed = false;
            if (_side == 0 || settling)
            {
                _side = d > PickDeadband ? 1 : d < -PickDeadband ? -1 : (_side != 0 ? _side : -1);
                seed = true;
            }
            else if (d * _side < -(seated ? LatchSeated : LatchStanding))
            {
                _side = -_side;
                seed = true;
            }
            // Across the bar, away from the body.
            FingerHint = seated ? (RightHand ? seatRight : -seatRight) : -Right * _side;

            float dy = shoulderY - Grip.y;
            float reachSq = 0.97f * armLength * 0.97f * armLength;
            float flat = Mathf.Sqrt(Mathf.Max(reachSq - dy * dy, 0f));
            float rho = flat + ReachSlack;

            if (!seated)
            {
                float gx = Vector3.Dot(Grip - _restGrip, Right) * _side;
                float tx = Vector3.Dot(Tip - _restGrip, Right) * _side;
                float gy = Vector3.Dot(Grip - _restGrip, Forward);
                _pivotF = Vector3.Dot(pivot - _restGrip, Forward);
                _pivotX = Vector3.Dot(pivot - _restGrip, Right) * _side;
                _tipF = Vector3.Dot(Tip - _restGrip, Forward);
                _tipX = tx;
                _endSide = Mathf.Max(gx, tx);
                if (seed)
                {
                    _bx = Vector3.Dot(bodyPos - _restGrip, Right) * _side;
                    _by = Vector3.Dot(bodyPos - _restGrip, Forward);
                }
                // Stay put while the end is in reach and the arm clear of the hips; otherwise move only as far as needed.
                // Swung away, the arm runs back from the end toward the body, so the body stops standing behind the end:
                // stepping out past the arm there would leave the end out of reach.
                float back = Mathf.Min(ForeBack, rho);
                back = Mathf.Lerp(back, Mathf.Min(back, BackSwungAway), -gx / SwungAwayBlend);
                _by = Mathf.Clamp(_by, gy - back, gy + Mathf.Min(ForeAhead, rho));
                MinSide = Mathf.Max(_endSide, ArmSide(_by)) + HipClear;
                float maxSide = gx + shoulderHalf + Mathf.Sqrt(Mathf.Max(rho * rho - (_by - gy) * (_by - gy), 0f));
                _bx = Mathf.Clamp(_bx, MinSide, Mathf.Max(MinSide, maxSide));   // clearance wins over reach
                StandPoint = _restGrip + Right * (_side * _bx) + Forward * _by;
            }

            if (!_logged && !settling)
            {
                _logged = true;
                Plugin.Log.LogInfo($"[Interaction] holding tiller '{control.name}' with the {(RightHand ? "right" : "left")} hand" +
                    (seated ? " from a seat" : $": end {dy:F2} m below the shoulders, flat reach {flat:F2} m, standing {_bx:F2} m beside it"));
            }
            return true;
        }
    }
}
