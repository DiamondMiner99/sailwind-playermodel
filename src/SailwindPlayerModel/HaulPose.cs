using System;
using System.Reflection;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The rope the local player holds with the Hand Over Hand mod, read from its public HandOverHandApi.TryGetHold by
    /// reflection, so neither mod needs the other. Without Hand Over Hand, or with one older than the API, nothing is held.
    /// </summary>
    internal static class HandOverHandBridge
    {
        private delegate bool HoldReader(out GPButtonRopeWinch winch, out Vector3 belay, out Vector3 hand, out float through, out bool running);

        private const string HandOverHandGuid = "com.diamondminer99.handoverhand";

        private static bool _looked;
        private static HoldReader _read;
        private static Func<RopeController, float> _ropeMeters;

        /// <summary>Hand Over Hand's meters of hand rope from one end of the rope's range to the other, or 0 without it.</summary>
        public static float RopeMeters(RopeController rope)
        {
            Find();
            if (_ropeMeters == null || rope == null) return 0f;
            try { return _ropeMeters(rope); }
            catch (Exception e)
            {
                _ropeMeters = null;
                Plugin.Log?.LogWarning("[Interaction] Hand Over Hand's rope travel could not be read: " + e.GetBaseException().Message);
                return 0f;
            }
        }

        public static bool TryGetHold(out GPButtonRopeWinch winch, out Vector3 belay, out Vector3 hand, out float through, out bool running)
        {
            winch = null;
            belay = hand = Vector3.zero;
            through = 0f;
            running = false;
            Find();
            if (_read == null) return false;
            try
            {
                return _read(out winch, out belay, out hand, out through, out running) && winch != null;
            }
            catch (Exception e)
            {
                _read = null;
                Plugin.Log?.LogWarning("[Interaction] Hand Over Hand's hold could not be read: " + e.GetBaseException().Message);
                return false;
            }
        }

        private static void Find()
        {
            if (_looked) return;
            _looked = true;
            try
            {
                // By Hand Over Hand's plugin entry, not a search of every loaded type, which logs a warning when it is absent.
                BepInEx.PluginInfo info;
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(HandOverHandGuid, out info) || info == null || info.Instance == null) return;
                Type api = info.Instance.GetType().Assembly.GetType("SailwindHandOverHand.HandOverHandApi", false);
                if (api == null) return;
                MethodInfo method = api.GetMethod("TryGetHold", BindingFlags.Public | BindingFlags.Static);
                if (method != null) _read = (HoldReader)Delegate.CreateDelegate(typeof(HoldReader), method, false);
                MethodInfo meters = api.GetMethod("RopeMeters", BindingFlags.Public | BindingFlags.Static);
                if (meters != null) _ropeMeters = (Func<RopeController, float>)Delegate.CreateDelegate(typeof(Func<RopeController, float>), meters, false);
                if (_read != null) Plugin.Log?.LogInfo("[Interaction] Hand Over Hand found: hands go on a rope held with it.");
            }
            catch (Exception e)
            {
                _read = null;
                _ropeMeters = null;
                Plugin.Log?.LogWarning("[Interaction] Hand Over Hand's hold could not be read: " + e.GetBaseException().Message);
            }
        }
    }

    /// <summary>
    /// Both hands on a rope hauled by hand from a winch (<see cref="InteractionKind.Haul"/>). The rope runs from where it
    /// leaves the deck (the belay) to where it ends in the hand. The hands sit on that line, a stroke apart, ahead of the
    /// hand point toward the belay. As rope comes in, one hand pulls back along the rope to the hand point while the other
    /// reaches forward above it and takes the next grip, hand over hand. The stroke follows the rope that comes in, not a
    /// clock, so the hands stop when the rope stops. When the rope runs out under right click, it slides through the
    /// hands and they stay put. Once the rope stops, the hands settle a stroke apart on it.
    ///
    /// For the local player the hand point and the rope through the hands come from Hand Over Hand. For a crewmate the
    /// hand point is in front of the eyes, the way Hand Over Hand places it, and the rope through the hands is worked out
    /// from the rope's length, which co-op keeps in step.
    /// </summary>
    internal sealed class HaulGrip
    {
        // Rope pulled per stroke of one hand, in meters.
        private const float Stroke = 0.3f;
        // How far the reaching hand clears the rope at the middle of its reach, in meters.
        private const float Lift = 0.07f;
        // The most strokes a second; faster rope slides through the hands.
        private const float MaxStrokesPerSecond = 2.5f;
        // For a crewmate, meters of rope per unit of the rope's length when Hand Over Hand is not installed here to say
        // (HandOverHandBridge.RopeMeters). It only sets how fast a crewmate's hands go round.
        private const float RemoteMetersPerLength = 5f;
        // How far in front of the eyes a crewmate's hand is, as Hand Over Hand's default HandReach.
        public const float RemoteHandReach = 0.6f;

        private float _phase;
        private float _lastThrough;
        private bool _haveThrough;
        private float _activity;
        private RopeController _rope;
        private float _lastLength;
        private float _remoteThrough;
        private float _remoteMeters;
        private LineRenderer _line;

        public Vector3 PalmR, PalmL, Axis;
        /// <summary>The hand nearer the belay, where a drawn rope reaches the hands.</summary>
        public Vector3 Front;

        /// <summary>Rope through the hands for a crewmate, from the change in the rope's length since the last frame.</summary>
        public float RemoteThrough(RopeController rope)
        {
            if (rope == null) return _remoteThrough;
            if (!ReferenceEquals(rope, _rope))
            {
                _rope = rope;
                _lastLength = rope.currentLength;
                float meters = HandOverHandBridge.RopeMeters(rope);
                _remoteMeters = meters > 0f && !float.IsNaN(meters) && !float.IsInfinity(meters) ? meters : RemoteMetersPerLength;
            }
            float length = rope.currentLength;
            _remoteThrough += (_lastLength - length) * _remoteMeters;
            _lastLength = length;
            return _remoteThrough;
        }

        public void Update(Vector3 belay, Vector3 hand, float through, bool running, Vector3 bodyRight, float dt)
        {
            float delta = _haveThrough ? through - _lastThrough : 0f;
            _lastThrough = through;
            _haveThrough = true;
            if (running || float.IsNaN(delta) || float.IsInfinity(delta)) delta = 0f;
            float most = MaxStrokesPerSecond * Stroke * Mathf.Max(dt, 0f);
            delta = Mathf.Clamp(delta, -most, most);

            bool moving = Mathf.Abs(delta) > 1e-5f;
            _activity = Mathf.MoveTowards(_activity, moving ? 1f : 0f, dt * (moving ? 8f : 3f));
            _phase = Mathf.Repeat(_phase + delta / Stroke, 2f);
            if (!moving)
            {
                // Settle a stroke apart: the hand that was pulling goes on to the hand point, or back to the front if it
                // had barely started, and the other takes the far end.
                float frac = _phase - Mathf.Floor(_phase);
                float goal = frac < 0.5f ? 0f : 1f;
                frac = Mathf.MoveTowards(frac, goal, dt * 1.5f);
                _phase = Mathf.Floor(_phase) + Mathf.Min(frac, 0.9999f);
            }

            Vector3 along = belay - hand;
            Axis = along.sqrMagnitude > 1e-6f ? along.normalized : Vector3.down;
            float p = _phase - Mathf.Floor(_phase);
            bool rightPulls = _phase < 1f;
            Vector3 pull = hand + Axis * (Stroke * (1f - p));
            Vector3 reach = hand + Axis * (Stroke * p) + LiftDir(rightPulls ? -bodyRight : bodyRight) * (Lift * Mathf.Sin(p * Mathf.PI) * _activity);
            PalmR = rightPulls ? pull : reach;
            PalmL = rightPulls ? reach : pull;
            Front = (1f - p) >= p ? pull : reach;
        }

        private Vector3 LiftDir(Vector3 side)
        {
            Vector3 lift = Vector3.ProjectOnPlane(Vector3.up + side * 0.6f, Axis);
            return lift.sqrMagnitude > 1e-6f ? lift.normalized : Vector3.up;
        }

        /// <summary>
        /// The rope from the belay to the front hand, drawn for a crewmate (Hand Over Hand draws the local player's own).
        /// It takes the look of the rope it continues.
        /// </summary>
        public void DrawRope(RopeController rope, Vector3 belay, Transform parent)
        {
            LineRenderer source = rope != null ? rope.GetComponent<LineRenderer>() : null;
            if (source == null || parent == null)
            {
                HideRope();
                return;
            }
            if (_line == null)
            {
                var go = new GameObject("HaulRope");
                go.transform.SetParent(parent, false);
                _line = go.AddComponent<LineRenderer>();
                _line.useWorldSpace = true;
                _line.positionCount = 2;
            }
            _line.gameObject.layer = source.gameObject.layer;
            _line.sharedMaterial = source.sharedMaterial;
            _line.textureMode = source.textureMode;
            _line.alignment = source.alignment;
            _line.colorGradient = source.colorGradient;
            _line.shadowCastingMode = source.shadowCastingMode;
            _line.receiveShadows = source.receiveShadows;
            _line.widthMultiplier = source.widthMultiplier;
            _line.startWidth = source.startWidth;
            _line.endWidth = source.endWidth;
            _line.SetPosition(0, belay);
            _line.SetPosition(1, Front);
            if (!_line.enabled) _line.enabled = true;
        }

        public void HideRope()
        {
            if (_line != null && _line.enabled) _line.enabled = false;
        }

        public void Release()
        {
            _haveThrough = false;
            _activity = 0f;
            _rope = null;
            HideRope();
        }

        public void Destroy()
        {
            Release();
            if (_line != null) UnityEngine.Object.Destroy(_line.gameObject);
            _line = null;
        }
    }
}
