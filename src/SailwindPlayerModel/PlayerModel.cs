using System;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The public surface other mods drive. Everything here is safe to call at any time: before a body exists,
    /// in a menu, during loading. Nothing throws, and the "no body" answers are all meaningful ones (false,
    /// null, no-op) rather than errors to guard against.
    ///
    /// <see cref="IsBodyAvailable"/> is false until the world has loaded and the body has been built from a
    /// port NPC, which normally happens within a couple of seconds of a save loading. Features built on this
    /// still need a path for the false case: menus, loading, and a session where no NPC could be found.
    /// </summary>
    public static class PlayerModel
    {
        /// <summary>
        /// True once a body exists and is posable. False in menus, during loading, and between scene loads.
        /// </summary>
        public static bool IsBodyAvailable
        {
            get
            {
                var b = LocalBody.Instance;
                return b != null && b.Body != null && b.Body.Instance != null;
            }
        }

        /// <summary>The local body, or null. Prefer the helpers here; this is for callers that need more.</summary>
        public static SyntyBody Body
        {
            get { return LocalBody.Instance != null ? LocalBody.Instance.Body : null; }
        }

        /// <summary>
        /// The local body's root transform, for whole-body position and orientation. Null when there is no
        /// body. Note that this mod rewrites the root's position and yaw every LateUpdate from the vanilla
        /// player, so a claimant that wants to move the whole body somewhere else must claim
        /// <see cref="PoseParts.Body"/> and write through its own callback, which runs after that.
        /// </summary>
        public static Transform Root
        {
            get
            {
                var b = Body;
                return b != null ? b.Root : null;
            }
        }

        /// <summary>A bone by name (Spine_01, UpperLeg_L, Shoulder_R, Head...), or null.</summary>
        public static Transform GetBone(string boneName)
        {
            var b = Body;
            return b != null ? b.GetBone(boneName) : null;
        }

        /// <summary>
        /// Take over some of the local body's bones. The body's own gait, crouch, look-lean and held-tool arm
        /// stand down for the parts named, and the optional write callback runs each LateUpdate at a defined
        /// point - after the unclaimed parts are posed, in ascending priority order. Dispose to hand them back.
        ///
        /// Returns null if there is no body yet (menus, loading), so check it rather than dereferencing.
        /// </summary>
        public static IDisposable ClaimPose(string owner, int priority = PosePriority.Ambient,
            PoseParts parts = PoseParts.All, Action<SyntyBody> write = null)
        {
            var b = Body;
            if (b == null)
            {
                Plugin.Log.LogInfo($"[PoseClaim] '{owner}' asked to claim {parts} but there is no body yet");
                return null;
            }
            return b.ClaimPose(owner, priority, parts, write);
        }

        /// <summary>
        /// Show the body regardless of the camera. Normally it is visible only in the ship-orbit camera and
        /// hidden in first person, where it would clip through the view. Pass true if you are moving the camera
        /// somewhere the body should be seen from.
        /// </summary>
        public static void ForceVisible(bool on)
        {
            if (LocalBody.Instance != null) LocalBody.Instance.ForcedVisible = on;
        }

        /// <summary>
        /// The control the local player is operating right now: the ship's wheel (<see cref="InteractionKind.Helm"/>),
        /// a rope winch or bilge pump (<see cref="InteractionKind.Crank"/>), or a sail pusher
        /// (<see cref="InteractionKind.Push"/>). None otherwise, with <paramref name="control"/> null. Read from
        /// the game's own pointer, so it works in first person as well as in the orbit camera.
        ///
        /// Co-op sends this to the crew. Carried items and mooring ropes are not reported here because they
        /// already have their own sync; feed those to a body with <see cref="SyntyBody.SetHeldItemPose"/> and
        /// <see cref="SyntyBody.SetInteraction"/>.
        /// </summary>
        public static InteractionKind GetLocalControl(out Transform control)
        {
            control = null;
            Transform target;
            PickupableItem held;
            var kind = LocalInteraction.Sample(out target, out held);
            if (kind != InteractionKind.Helm && kind != InteractionKind.Crank && kind != InteractionKind.Push)
                return InteractionKind.None;
            control = target;
            return kind;
        }

        /// <summary>
        /// Whether the local player is sitting, and where: the hip joints, the facing, where the legs go, and the
        /// height of the floor under the feet. Co-op sends this to the crew; feed it to a body with
        /// <see cref="SyntyBody.SetSeat"/>.
        /// </summary>
        public static bool TryGetLocalSeat(out Vector3 hipsWorld, out Vector3 forwardWorld, out SeatPose pose, out float floorWorldY)
        {
            return Seating.TryGetSeat(out hipsWorld, out forwardWorld, out pose, out floorWorldY);
        }

        /// <summary>
        /// Whether the local player is lying in one of the game's beds, and where: the head, the direction the
        /// feet point, and the direction the chest faces. Feed it to a body with <see cref="SyntyBody.SetLying"/>.
        /// </summary>
        public static bool TryGetLocalLying(out Vector3 headWorld, out Vector3 alongWorld, out Vector3 upWorld)
        {
            return Seating.TryGetLying(out headWorld, out alongWorld, out upWorld);
        }

        /// <summary>
        /// Knock the local player off their feet. The body really falls, with the view riding it, onto whatever is
        /// under it, lies there for <paramref name="seconds"/> after landing, and gets up where it landed.
        /// <paramref name="impulse"/> is the push in meters per second, in world space as the deck is drawn, pointing the
        /// way the body should go; about 3 is a hard shove. Called again while already down, the body is pushed again
        /// and stays down at least that long. Returns false, with the reason logged, when the player cannot fall right
        /// now (in a menu, in bed, swimming, the shipyard, or no room to fall).
        /// </summary>
        public static bool GoDown(DownedReason reason, Vector3 impulse, float seconds)
        {
            return Downed.Instance != null && Downed.Instance.GoDown(reason, impulse, seconds);
        }

        /// <summary>True while the local player is knocked down, from going over until they start getting up.</summary>
        public static bool IsLocalDowned { get { return Downed.IsDown; } }

        /// <summary>
        /// The local player's ragdoll while down: the pelvis, each part's rotation in <see cref="RagdollPart"/> order
        /// (<paramref name="rotationsWorld"/> must be at least <see cref="SyntyBody.RagdollParts"/> long), about where the
        /// floor is under it, and what the arms are doing. Co-op sends this to the crew; feed it to a body with
        /// <see cref="SyntyBody.SetRagdoll"/>.
        /// </summary>
        public static bool TryGetLocalRagdoll(out Vector3 pelvisWorld, Quaternion[] rotationsWorld, out float floorWorldY, out FallReaction reaction)
        {
            return Downed.TryGetRagdoll(out pelvisWorld, rotationsWorld, out floorWorldY, out reaction);
        }

        /// <summary>
        /// How far through getting back up the local player is, 0 to 1. Feed it to a body with
        /// <see cref="SyntyBody.SetGettingUp"/>, after the ragdoll pose it gets up from.
        /// </summary>
        public static bool TryGetLocalGettingUp(out float progress01)
        {
            return Downed.TryGetGettingUp(out progress01);
        }

        /// <summary>
        /// Smoke, flames and steam (each 0 to 1) coming off the local player's pants from sitting on a lit stove too
        /// long. All zero almost always. Feed them to a body with <see cref="SyntyBody.SetScorch"/>.
        /// </summary>
        public static void GetLocalScorch(out float smoke01, out float fire01, out float steam01)
        {
            smoke01 = Seating.Smoke01;
            fire01 = Seating.Fire01;
            steam01 = Seating.Steam01;
        }

        // ---- appearance ---------------------------------------------------------------------------------

        private static PlayerAppearance _localAppearance;
        private static bool _localAppearanceLoaded;

        /// <summary>
        /// Supplies a starting look when the config setting is empty, so a player who has never opened a
        /// character screen is not slot 0 like everyone else. Co-op sets this to a look derived from the
        /// player's own Steam id.
        ///
        /// Return null to mean "cannot answer yet". The result is then NOT latched and this is asked again on
        /// the next read - which matters, because a provider seeded from a Steam id that is not up yet would
        /// otherwise freeze the all-zero seed for the whole process and make every player in that situation
        /// identical, the precise outcome the default exists to avoid.
        /// </summary>
        public static Func<PlayerAppearance?> DefaultAppearanceProvider;

        /// <summary>
        /// What the local player looks like. Read from config on first use and written back on set, so it
        /// survives restarts. The co-op mod writes this from its character screen and puts it on the wire.
        /// </summary>
        public static PlayerAppearance LocalAppearance
        {
            get
            {
                if (!_localAppearanceLoaded)
                {
                    string raw = BodyTuning.Appearance != null ? BodyTuning.Appearance.Value : null;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        _localAppearance = PlayerAppearance.Deserialize(raw);
                        _localAppearanceLoaded = true;
                    }
                    else
                    {
                        var provider = DefaultAppearanceProvider;
                        PlayerAppearance? seeded = null;
                        if (provider != null)
                        {
                            try { seeded = provider(); }
                            catch (Exception e) { Plugin.Log.LogWarning("[PlayerModel] Default appearance provider threw: " + e); }
                        }
                        if (seeded.HasValue)
                        {
                            _localAppearance = seeded.Value;
                            _localAppearanceLoaded = true;   // latch only once someone could actually answer
                        }
                        else
                        {
                            _localAppearance = PlayerAppearance.Default();
                            if (provider == null) _localAppearanceLoaded = true;
                        }
                    }
                }
                return _localAppearance;
            }
            set
            {
                _localAppearance = value;
                _localAppearanceLoaded = true;
                BodyTuning.Appearance.Value = value.Serialize();
                ApplyAppearanceLive();
                var handler = LocalAppearanceChanged;
                if (handler != null)
                {
                    try { handler(); }
                    catch (Exception e) { Plugin.Log.LogWarning("[PlayerModel] An appearance listener threw: " + e); }
                }
            }
        }

        /// <summary>Raised after <see cref="LocalAppearance"/> changes, so co-op can tell the crew.</summary>
        public static event Action LocalAppearanceChanged;

        /// <summary>
        /// The vanilla customizer on OUR body clone, or null if no body exists yet. This is the only honest
        /// source of "how many variants does THIS machine have" - the counts come from the part lists on the
        /// actual clone, not from a compiled-in table that could drift from it.
        /// </summary>
        public static PsychoticLab.CharacterCustomizer GetCustomizer()
        {
            var b = Body;
            if (b == null || b.Instance == null) return null;
            return b.Instance.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true);
        }

        /// <summary>Re-dress the local body in place. See <see cref="SyntyBody.RefreshAppearance"/>.</summary>
        public static void ApplyAppearanceLive()
        {
            var b = Body;
            if (b != null) b.RefreshAppearance(LocalAppearance);
        }

        // ---- identity -----------------------------------------------------------------------------------

        private static string _localDisplayName = "";

        /// <summary>
        /// Name shown over your own body. Empty by default - a solo player does not need a label over their
        /// own head - and set by the co-op mod so a screenshot of the crew has everyone labelled the same way.
        /// </summary>
        public static string LocalDisplayName
        {
            get { return _localDisplayName; }
            set
            {
                _localDisplayName = value ?? "";
                if (LocalBody.Instance != null) LocalBody.Instance.SetDisplayName(_localDisplayName);
            }
        }
    }
}
