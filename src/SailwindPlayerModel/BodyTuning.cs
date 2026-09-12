using BepInEx.Configuration;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Live tuning for how a body stands, crouches and leans. Every value is a config entry so the pose can be
    /// adjusted in-game from the F1 Configuration Manager rather than through a rebuild.
    ///
    /// These knobs drive EVERY body this mod builds - your own third-person one and, when the co-op mod is
    /// installed, your crewmates'. That is the point of them living here: a player's own body and the body
    /// their crew sees must squat the same way.
    /// </summary>
    public static class BodyTuning
    {
        private const string SecPose = "1. Pose";
        private const string SecAppearance = "3. Appearance";
        private const string SecMenu = "4. Menu";

        public static ConfigEntry<float> CrouchDropMeters { get; private set; }
        public static ConfigEntry<float> CrouchTorsoLeanDeg { get; private set; }
        public static ConfigEntry<float> CrouchArmBendDeg { get; private set; }
        public static ConfigEntry<float> CrouchStrideCut { get; private set; }
        public static ConfigEntry<float> CrouchKneeForward { get; private set; }
        public static ConfigEntry<float> CrouchThighLiftDeg { get; private set; }
        public static ConfigEntry<float> CrouchHipSetbackMaxMeters { get; private set; }
        public static ConfigEntry<float> SoleOffsetMeters { get; private set; }
        public static ConfigEntry<float> LookPitchScale { get; private set; }
        public static ConfigEntry<float> LookPitchMaxDeg { get; private set; }
        public static ConfigEntry<string> Appearance { get; private set; }
        public static ConfigEntry<float> MenuButtonScale { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            // The crouch is a SQUAT: the body drops and 2-bone leg IK re-plants the feet at their standing
            // spot. Everything here is applied times the 0..1 crouch amount.
            CrouchDropMeters = cfg.Bind(SecPose, "CrouchDropMeters", 0.6f,
                new ConfigDescription("Squat depth: how far the hips/body drop at full crouch. The leg IK keeps the feet planted on the deck at any depth, so the head comes down toward the camera without the feet clipping through.",
                    new AcceptableValueRange<float>(0f, 1.2f)));
            CrouchTorsoLeanDeg = cfg.Bind(SecPose, "CrouchTorsoLeanDeg", 28f,
                new ConfigDescription("Forward torso fold at full crouch (about the body's world right axis) - brings the chest/head down and forward toward the camera. Negative leans back.",
                    new AcceptableValueRange<float>(-80f, 80f)));
            CrouchArmBendDeg = cfg.Bind(SecPose, "CrouchArmBendDeg", 45f,
                new ConfigDescription("Elbow flex for a ready/tactical arm pose at full crouch (composed on top of the walk arm swing; the upper arms also raise slightly). Negative flexes the other way.",
                    new AcceptableValueRange<float>(-120f, 120f)));
            CrouchStrideCut = cfg.Bind(SecPose, "CrouchStrideCut", 0.5f,
                new ConfigDescription("Fraction the walk stride shrinks while crouched (crouch-walk).",
                    new AcceptableValueRange<float>(0f, 0.95f)));
            CrouchKneeForward = cfg.Bind(SecPose, "CrouchKneeForward", 1f,
                new ConfigDescription("Knee-forward pole sign for the leg IK. +1 bends the knees FORWARD (a squat). If the knees bend the wrong way (backward), set this to -1 to flip the pole live.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            // SQUAT vs SEIZA. The crouch used to drop the hips straight down while the feet stayed planted
            // directly beneath them, which is kneeling geometry, not squatting - the reported "looks like I'm
            // sitting on my own feet". A real squat sends the hips BACKWARD as they drop. That is also the
            // ONLY lever available: with the hip above the foot the knee lies on a horizontal circle, so the
            // IK pole sets the knee's compass direction but never its height, and with roughly equal
            // thigh/shin bones the knee can never rise above the hip at all. Moving the hip back is what opens
            // the thigh angle up; the shins and ankles then re-solve to follow.
            CrouchThighLiftDeg = cfg.Bind(SecPose, "CrouchThighLiftDeg", 8f,
                new ConfigDescription("How far the thigh lifts toward the chest at full crouch, in degrees above the hip-to-ankle line. The hip setback needed to achieve it is solved from your rig's own measured bone lengths, so the same angle looks the same on any character - which is why this is an angle and not a distance. 0 keeps the old straight-down drop.",
                    new AcceptableValueRange<float>(-30f, 25f)));
            CrouchHipSetbackMaxMeters = cfg.Bind(SecPose, "CrouchHipSetbackMaxMeters", 0.35f,
                new ConfigDescription("Safety clamp (meters) on how far back the hips may travel for CrouchThighLiftDeg. SET THIS TO 0 to disable the squat setback entirely and get the previous straight-down crouch back, without needing a new build.",
                    new AcceptableValueRange<float>(0f, 0.6f)));

            // Ground plant. The body used to be planted with a hardcoded 0.9m guess at the distance from the
            // player root down to the ground; the real distance is read live from the vanilla
            // CharacterController instead (VanillaPlayer.ControllerFeetGap). This knob is only the residual
            // nudge on top.
            SoleOffsetMeters = cfg.Bind(SecPose, "SoleOffsetMeters", 0f,
                new ConfigDescription("Fine adjustment (meters) to how high bodies stand relative to the surface under them. POSITIVE raises, NEGATIVE sinks. Leave at 0 unless bodies visibly hover above or sink into decks; the base value is measured from the game rather than assumed. Takes effect on the next body build (leave and re-enter third person, or rejoin).",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));

            // LOOK-LEAN. The upper body (Spine_01 -> chest/head/arms) pitches on the hips toward where the
            // player looks vertically, in every state, composed on top of the crouch fold.
            LookPitchScale = cfg.Bind(SecPose, "LookPitchScale", 0.9f,
                new ConfigDescription("Torso look-lean: fraction of your vertical look angle the upper body pitches on the hips (1.0 = follows your look 1:1). Looking DOWN folds the torso forward, looking UP leans it back. Set NEGATIVE to flip the direction if it bends the wrong way in-game.",
                    new AcceptableValueRange<float>(-2f, 2f)));
            LookPitchMaxDeg = cfg.Bind(SecPose, "LookPitchMaxDeg", 55f,
                new ConfigDescription("Clamp (degrees) on the torso look-lean so it never over-bends up or down. Must exceed the crouch fold (about 28 deg) for the torso to lean BACK past vertical while crouched and looking up.",
                    new AcceptableValueRange<float>(0f, 90f)));

            MenuButtonScale = cfg.Bind(SecMenu, "MenuButtonScale", 1f,
                new ConfigDescription("Size of the buttons on the in-game pause menu, relative to vanilla. Default 1.0 is vanilla-sized; the parchment is made taller to fit them rather than the buttons being shrunk to fit the parchment. Lower it if you would rather have a shorter scroll. Applies the next time the menu lays out (open the pause menu again).",
                    new AcceptableValueRange<float>(0.5f, 1f)));

            Appearance = cfg.Bind(SecAppearance, "Character", "",
                "Your character's appearance, as \"slot=variant\" pairs (e.g. \"gender=1;hair=3;torso=7\"). Normally written by an in-game character screen rather than edited here. Unknown slot names are ignored and out-of-range variants fall back to that slot's default, so a hand-edited value can never produce an invisible or broken character.");
        }
    }
}
