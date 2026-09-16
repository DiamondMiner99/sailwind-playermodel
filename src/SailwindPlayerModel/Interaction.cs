using BepInEx.Configuration;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>What a body is doing with its hands this frame.</summary>
    public enum InteractionKind
    {
        None,
        /// <summary>A carried item. How it is held depends on the item; see ItemPoses. Fed through SyntyBody.SetHeldItemPose.</summary>
        Carry,
        /// <summary>A big carried item such as a crate or barrel. Treated the same as Carry; kept for callers that report it.</summary>
        CarryBig,
        /// <summary>A mooring rope or its length adjuster: both hands on the rope, one ahead of the other.</summary>
        Rope,
        /// <summary>The ship's wheel: both hands on its handles, hand over hand.</summary>
        Helm,
        /// <summary>A rope winch (halyards, sheets, the anchor) or the bilge pump: both hands on the handles, turning with them.</summary>
        Crank,
        /// <summary>A sail pusher: both hands pressed against it.</summary>
        Push,
    }

    /// <summary>Live tuning for the control poses. Every value applies immediately from the F1 menu.</summary>
    public static class InteractionTuning
    {
        private const string Section = "5. Interactions";

        public static ConfigEntry<bool> Enabled { get; private set; }
        public static ConfigEntry<float> BlendSpeed { get; private set; }
        public static ConfigEntry<float> ReachFraction { get; private set; }
        public static ConfigEntry<float> StepInMaxMeters { get; private set; }
        public static ConfigEntry<bool> TurnToFace { get; private set; }
        public static ConfigEntry<float> ElbowDown { get; private set; }
        public static ConfigEntry<float> ElbowOut { get; private set; }
        public static ConfigEntry<float> HelmHandAngle { get; private set; }
        public static ConfigEntry<float> HelmReleaseArc { get; private set; }
        public static ConfigEntry<float> CrankHandAngle { get; private set; }
        public static ConfigEntry<float> StandFraction { get; private set; }
        public static ConfigEntry<float> RopeReach { get; private set; }
        public static ConfigEntry<float> RopeHandGap { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "Enabled", true,
                "Pose the body at the ship's wheel, winches, bilge pump and sail pushers, holding mooring ropes, and carrying items.");
            BlendSpeed = cfg.Bind(Section, "BlendSpeed", 9f,
                new ConfigDescription("How fast the arms move onto a control or item and back off it.",
                    new AcceptableValueRange<float>(1f, 30f)));
            ReachFraction = cfg.Bind(Section, "ReachFraction", 0.8f,
                new ConfigDescription("Sail pushers: how much of the arm's length to use before the body steps closer. Lower keeps the elbows bent.",
                    new AcceptableValueRange<float>(0.4f, 1f)));
            StepInMaxMeters = cfg.Bind(Section, "StepInMaxMeters", 1.0f,
                new ConfigDescription("Farthest the body moves to stand at a control, closer or farther, in meters. 0 turns this off.",
                    new AcceptableValueRange<float>(0f, 2f)));
            TurnToFace = cfg.Bind(Section, "TurnToFace", true,
                "Turn the body to face the wheel, winch, pump or sail pusher being used.");
            ElbowDown = cfg.Bind(Section, "ElbowDown", 1.0f,
                new ConfigDescription("Elbow direction: how strongly the elbows point down.",
                    new AcceptableValueRange<float>(-1f, 2f)));
            ElbowOut = cfg.Bind(Section, "ElbowOut", 0.6f,
                new ConfigDescription("Elbow direction: how strongly the elbows point out to the sides.",
                    new AcceptableValueRange<float>(-1f, 2f)));
            HelmHandAngle = cfg.Bind(Section, "HelmHandAngle", 55f,
                new ConfigDescription("Where each hand wants to be on the ship's wheel, in degrees from the top. A hand stays on its handle as the wheel turns and takes the next one when it has turned too far.",
                    new AcceptableValueRange<float>(10f, 110f)));
            HelmReleaseArc = cfg.Bind(Section, "HelmReleaseArc", 70f,
                new ConfigDescription("How far a wheel handle turns past where the hand wants to be before the hand lets go and takes the next handle, in degrees.",
                    new AcceptableValueRange<float>(25f, 150f)));
            CrankHandAngle = cfg.Bind(Section, "CrankHandAngle", 60f,
                new ConfigDescription("Where each hand first takes hold of a winch, in degrees from the top. Small winches and pumps are then turned all the way round without letting go.",
                    new AcceptableValueRange<float>(10f, 110f)));
            StandFraction = cfg.Bind(Section, "StandFraction", 0.62f,
                new ConfigDescription("How far back from a wheel or winch the body stands, as a fraction of arm length. Higher stands farther away.",
                    new AcceptableValueRange<float>(0.3f, 1.2f)));
            RopeReach = cfg.Bind(Section, "RopeReach", 0.42f,
                new ConfigDescription("How far in front of the chest the leading hand holds a mooring rope (meters).",
                    new AcceptableValueRange<float>(0.15f, 0.7f)));
            RopeHandGap = cfg.Bind(Section, "RopeHandGap", 0.16f,
                new ConfigDescription("Distance between the two hands on a mooring rope (meters).",
                    new AcceptableValueRange<float>(0f, 0.4f)));
        }
    }

    /// <summary>
    /// Hand positions for the controls that are not rotors.
    /// Wheels, winches, windlasses and pumps are handled by <see cref="RotorGrip"/>; carried items by ItemPoses.
    /// </summary>
    internal static class InteractionGeometry
    {
        /// <summary>Both hands pressed on the near face of a sail pusher, a shoulder's width apart.</summary>
        public static void Push(Transform pusher, Vector3 chest, Vector3 bodyRight, out Vector3 left, out Vector3 right)
        {
            var col = pusher.GetComponent<Collider>();
            // The collider's AABB, not Collider.ClosestPoint: that one refuses non-convex mesh colliders.
            Vector3 p = col != null ? col.bounds.ClosestPoint(chest) : pusher.position;
            right = p + bodyRight * 0.17f;
            left = p - bodyRight * 0.17f;
        }

        /// <summary>Both hands on a mooring rope in front of the chest, the right one leading toward the rope.</summary>
        public static void Rope(Vector3 ropePoint, Vector3 chest, Vector3 bodyForward, Vector3 bodyRight, out Vector3 left, out Vector3 right)
        {
            Vector3 dir = ropePoint - chest;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : bodyForward;
            // Do not let the rope pull the hands behind the body or straight up.
            if (Vector3.Dot(dir, bodyForward) < 0.2f) dir = (bodyForward + dir * 0.3f).normalized;
            float reach = InteractionTuning.RopeReach.Value;
            float gap = InteractionTuning.RopeHandGap.Value;
            Vector3 down = Vector3.down * 0.12f;
            right = chest + dir * reach + down + bodyRight * 0.04f;
            left = chest + dir * Mathf.Max(0.08f, reach - gap) + down - bodyRight * 0.04f;
        }
    }
}
