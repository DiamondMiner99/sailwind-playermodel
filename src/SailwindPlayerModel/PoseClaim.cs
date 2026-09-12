using System;
using System.Collections.Generic;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Which parts of the skeleton a pose claim takes over. Claims name parts rather than the whole body so a
    /// downed body can still have, say, one arm driven by something else later without either writer having to
    /// know about the other.
    /// </summary>
    [Flags]
    public enum PoseParts
    {
        None = 0,
        /// <summary>UpperLeg/LowerLeg/Foot, both sides: the walk gait's leg swing and the crouch leg IK.</summary>
        Legs = 1 << 0,
        /// <summary>Spine_01: the idle breathe, the crouch fold and the look-lean, which all pivot the upper body.</summary>
        Spine = 1 << 1,
        LeftArm = 1 << 2,
        /// <summary>Shoulder_R/Elbow_R/Hand_R: the gait arm swing and the held-tool arm IK.</summary>
        RightArm = 1 << 3,
        /// <summary>The body clone's own local position inside the root: the crouch drop and hip setback.</summary>
        Body = 1 << 4,
        Arms = LeftArm | RightArm,
        All = Legs | Spine | Arms | Body,
    }

    /// <summary>
    /// Conventional priorities. Higher wins: a claim suppresses the body's own animation for the parts it
    /// names, and among claims the highest-priority write runs last, so it is the one left on the bones.
    /// These are plain ints, so a caller with an unusual need can sit between two of them.
    /// </summary>
    public static class PosePriority
    {
        /// <summary>Ambient poses: an idle flourish, a lean on a rail.</summary>
        public const int Ambient = 10;
        /// <summary>Holding or using something: the held-tool arm.</summary>
        public const int Holding = 20;
        /// <summary>Sitting, lying in a bed: a pose the player chose and can leave.</summary>
        public const int Seated = 40;
        /// <summary>Knocked out, shoved over, dead. Beats everything.</summary>
        public const int Downed = 100;
    }

    /// <summary>
    /// A live claim on some of a body's bones. Dispose it to hand them back. Never throws on a double dispose.
    ///
    /// The optional <see cref="Write"/> callback is the reason this is not just a boolean. Unity does not
    /// order LateUpdate between components, so a claimant posing bones from its own LateUpdate would land
    /// either side of the body's animation depending on load order. Handing the write to the body instead
    /// means it runs at a defined point - after the parts nobody claimed are posed, in ascending priority -
    /// and the ordering question never arises.
    /// </summary>
    public sealed class PoseClaim : IDisposable
    {
        public string Owner { get; private set; }
        public int Priority { get; private set; }
        public PoseParts Parts { get; private set; }
        public Action<SyntyBody> Write { get; private set; }
        public bool Released { get; private set; }

        private readonly PoseStack _stack;

        internal PoseClaim(PoseStack stack, string owner, int priority, PoseParts parts, Action<SyntyBody> write)
        {
            _stack = stack;
            Owner = owner;
            Priority = priority;
            Parts = parts;
            Write = write;
        }

        public void Dispose()
        {
            if (Released) return;
            Released = true;
            if (_stack != null) _stack.Release(this);
        }
    }

    /// <summary>
    /// The claims currently held on one body, kept sorted by ascending priority.
    ///
    /// Claim and release are LOGGED with the owner string. Two systems fighting over a pose with no way to see
    /// who is holding it is a miserable thing to debug, and this rig now has four procedural writers on it
    /// before any other mod joins in.
    /// </summary>
    public sealed class PoseStack
    {
        private readonly List<PoseClaim> _claims = new List<PoseClaim>();
        private readonly string _bodyName;
        private PoseParts _suppressed;

        public PoseStack(string bodyName) { _bodyName = bodyName; }

        /// <summary>Parts currently taken by a claim, so the body's own animation stands down for them.</summary>
        public PoseParts Suppressed { get { return _suppressed; } }

        public bool IsSuppressed(PoseParts parts) { return (_suppressed & parts) != 0; }

        public int Count { get { return _claims.Count; } }

        public PoseClaim Claim(string owner, int priority, PoseParts parts, Action<SyntyBody> write)
        {
            var claim = new PoseClaim(this, string.IsNullOrEmpty(owner) ? "<unnamed>" : owner, priority, parts, write);
            // Ascending priority; a later claim at equal priority sorts after the earlier one, so the most
            // recent writer wins a tie. Insertion sort - this list is single digits long by construction.
            int i = _claims.Count;
            while (i > 0 && _claims[i - 1].Priority > priority) i--;
            _claims.Insert(i, claim);
            Recompute();
            Plugin.Log.LogInfo($"[PoseClaim] {_bodyName}: '{claim.Owner}' claimed {parts} at priority {priority} ({_claims.Count} claim(s) held)");
            return claim;
        }

        internal void Release(PoseClaim claim)
        {
            if (!_claims.Remove(claim)) return;
            Recompute();
            Plugin.Log.LogInfo($"[PoseClaim] {_bodyName}: '{claim.Owner}' released {claim.Parts} ({_claims.Count} claim(s) held)");
        }

        /// <summary>Drop every claim (the body is being destroyed). Claimants see Released go true.</summary>
        public void Clear()
        {
            for (int i = 0; i < _claims.Count; i++)
            {
                Plugin.Log.LogInfo($"[PoseClaim] {_bodyName}: dropping '{_claims[i].Owner}' ({_claims[i].Parts}) - body destroyed");
            }
            _claims.Clear();
            _suppressed = PoseParts.None;
        }

        /// <summary>Run every claimant's write callback in ascending priority, so the highest wins the bones.</summary>
        public void RunWrites(SyntyBody body)
        {
            for (int i = 0; i < _claims.Count; i++)
            {
                var w = _claims[i].Write;
                if (w == null) continue;
                try { w(body); }
                catch (Exception e)
                {
                    // One bad claimant must not stop the rest of the crew animating. Drop the claim rather
                    // than throwing the same exception every frame for the life of the session.
                    Plugin.Log.LogError($"[PoseClaim] {_bodyName}: '{_claims[i].Owner}' threw while posing, releasing its claim: {e}");
                    _claims[i].Dispose();
                    i--;
                }
            }
        }

        private void Recompute()
        {
            var parts = PoseParts.None;
            for (int i = 0; i < _claims.Count; i++) parts |= _claims[i].Parts;
            _suppressed = parts;
        }
    }
}
