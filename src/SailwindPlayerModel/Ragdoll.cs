using System.Collections.Generic;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>The parts of a ragdolled body, parents before children, which is the order they are posed in.</summary>
    public enum RagdollPart
    {
        Pelvis, Chest, Head, UpperArmL, ForearmL, UpperArmR, ForearmR, ThighL, ShinL, ThighR, ShinR,
    }

    /// <summary>What a falling body does with its arms.</summary>
    public enum FallReaction : byte
    {
        Limp = 0,
        CoverFace = 1,
        CoverHead = 2,
        Flail = 3,
    }

    /// <summary>
    /// The physics half of a knocked-down body: eleven rigidbodies joined at the neck, waist, shoulders, elbows, hips and
    /// knees, sized from the body's own bones when it went down. Nothing here is drawn. The body's bones copy these
    /// parts every frame (see <see cref="SyntyBody.SetRagdoll"/>).
    ///
    /// Built in the frame the physics controller walks in, which aboard is the boat's walking copy, and read back out in
    /// the frame the deck is drawn in: the two share local coordinates, so a part's local pose is the bone's local pose.
    /// When the game moves the player between frames mid-fall (off a boat, into the sea), every part is moved to the new
    /// frame where it was on screen, velocities and all, and the fall carries on.
    ///
    /// About 2.7 kg in all, for the same reason the old single capsule was 2 kg: parented into a boat's walking copy,
    /// weight pushes the hull, and in co-op a crewmate's machine would be pushing the captain's boat. Gravity does not
    /// care; the solver iterations buy back the resistance to sinking into a moving deck.
    ///
    /// With no body to size it from (no rig found), it is a single body-sized capsule instead, which still gives the
    /// first-person view something real to fall with.
    /// </summary>
    internal sealed class Ragdoll
    {
        public const int Count = 11;
        private const string PartName = "PlayerModelRagdoll";

        private static readonly int[] Parents = { -1, 0, 1, 1, 3, 1, 5, 0, 7, 0, 9 };
        // The head is heavier than it looks on purpose: a light head on a tight neck chatters against a deck it is
        // pressed into, which read as the head wobbling in some places and not others.
        private static readonly float[] Masses = { 0.55f, 0.55f, 0.45f, 0.14f, 0.1f, 0.14f, 0.1f, 0.3f, 0.2f, 0.3f, 0.2f };

        private readonly Rigidbody[] _parts;
        private readonly List<Collider> _colliders = new List<Collider>();
        private Transform _visual, _physics;
        private Vector3 _eyeLocal, _headUpLocal, _headFwdLocal = Vector3.forward;
        private Vector3 _lastPelvis;

        public bool Full { get { return _parts.Length == Count; } }
        public Transform Visual { get { return _visual; } }
        public Transform Physics { get { return _physics; } }
        public Rigidbody Pelvis { get { return _parts[0]; } }
        public Transform PartTransform(int i) { return _parts[Mathf.Clamp(i, 0, _parts.Length - 1)].transform; }

        private Ragdoll(int count, Transform visual, Transform physics)
        {
            _parts = new Rigidbody[count];
            _visual = visual;
            _physics = physics;
        }

        // ---- building ---------------------------------------------------------------------------------------

        /// <summary>A full ragdoll from the body's bones as they are posed right now. Null with a reason when the rig is missing a bone.</summary>
        public static Ragdoll Build(SyntyBody body, Vector3 facingVisual, Transform visual, Transform physics, out string why)
        {
            why = null;
            var bones = new Transform[Count];
            for (int i = 0; i < Count; i++)
            {
                bones[i] = body.RagdollBone((RagdollPart)i);
                if (bones[i] == null) { why = "the body has no " + (RagdollPart)i + " bone"; return null; }
            }
            Transform handL = body.HandL, handR = body.HandR;
            Transform footL = body.GetBone("Foot_L") ?? body.GetBone("Ankle_L"), footR = body.GetBone("Foot_R") ?? body.GetBone("Ankle_R");

            var r = new Ragdoll(Count, visual, physics);
            Vector3 up = Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(facingVisual, up).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            Vector3 right = Vector3.Cross(up, fwd);

            Vector3 pelvis = bones[0].position;
            Vector3 hips = (bones[(int)RagdollPart.ThighL].position + bones[(int)RagdollPart.ThighR].position) * 0.5f;
            Vector3 shoulders = (bones[(int)RagdollPart.UpperArmL].position + bones[(int)RagdollPart.UpperArmR].position) * 0.5f;
            Vector3 head = bones[(int)RagdollPart.Head].position;
            float hipWidth = Vector3.Distance(bones[(int)RagdollPart.ThighL].position, bones[(int)RagdollPart.ThighR].position);
            float shoulderWidth = Vector3.Distance(bones[(int)RagdollPart.UpperArmL].position, bones[(int)RagdollPart.UpperArmR].position);

            for (int i = 0; i < Count; i++)
            {
                var go = new GameObject(PartName + " " + (RagdollPart)i);
                go.layer = 2;
                go.transform.SetParent(physics, false);
                go.transform.position = physics.TransformPoint(visual.InverseTransformPoint(bones[i].position));
                go.transform.rotation = physics.rotation * Quaternion.Inverse(visual.rotation) * bones[i].rotation;
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = Masses[i];
                r.Configure(rb);
                r._parts[i] = rb;
            }

            // Shapes, from the bones in the drawn frame. Offsets and turns are taken relative to each bone, which is the
            // same relative to its part whichever frame the part is in.
            Vector3 torsoUp = (shoulders - hips).sqrMagnitude > 1e-4f ? (shoulders - hips).normalized : up;
            r.Box(0, bones[0], Vector3.Lerp(hips, shoulders, 0.12f), fwd, torsoUp, new Vector3(hipWidth + 0.14f, 0.24f, 0.2f));
            Vector3 neck = head - torsoUp * 0.04f;
            r.Box(1, bones[1], Vector3.Lerp(bones[1].position, neck, 0.5f), fwd, torsoUp,
                new Vector3(shoulderWidth + 0.04f, Mathf.Max(0.2f, Vector3.Distance(bones[1].position, neck)), 0.22f));
            r.Sphere(2, bones[2], head + up * 0.09f + fwd * 0.02f, 0.11f);
            r.Limb(3, bones[3], bones[4].position, 0.05f, 0f);
            r.Limb(4, bones[4], handL != null ? handL.position : bones[4].position - up * 0.25f, 0.045f, 0.08f);
            r.Limb(5, bones[5], bones[6].position, 0.05f, 0f);
            r.Limb(6, bones[6], handR != null ? handR.position : bones[6].position - up * 0.25f, 0.045f, 0.08f);
            r.Limb(7, bones[7], bones[8].position, 0.075f, 0f);
            // Shins stop at the ankle: any lower and a body standing on the deck starts inside it and is thrown up.
            r.Limb(8, bones[8], footL != null ? footL.position : bones[8].position - up * 0.42f, 0.055f, -0.03f);
            r.Limb(9, bones[9], bones[10].position, 0.075f, 0f);
            r.Limb(10, bones[10], footR != null ? footR.position : bones[10].position - up * 0.42f, 0.055f, -0.03f);

            // Joints. Twist is about the body's right: positive swings a limb forward, the way Unity's ragdoll builder
            // sets up hips (-20..70) and knees (-80..0). The arms start hanging, so their twist is the forward and back
            // swing and their first swing axis lifts them out to the side.
            r.Joint(1, right, fwd, -25f, 35f, 12f, 15f);    // waist
            r.Joint(2, right, fwd, -45f, 45f, 32f, 40f);    // neck
            r.Joint(3, right, fwd, -60f, 140f, 80f, 40f);   // left shoulder
            r.Joint(4, right, fwd, -5f, 140f, 0f, 10f);     // left elbow
            r.Joint(5, right, fwd, -60f, 140f, 80f, 40f);   // right shoulder
            r.Joint(6, right, fwd, -5f, 140f, 0f, 10f);     // right elbow
            r.Joint(7, right, fwd, -20f, 95f, 30f, 15f);    // left hip
            r.Joint(8, right, fwd, -120f, 0f, 0f, 5f);      // left knee
            r.Joint(9, right, fwd, -20f, 95f, 30f, 15f);    // right hip
            r.Joint(10, right, fwd, -120f, 0f, 0f, 5f);     // right knee

            // The view rides the head: the eyes a little over the head bone and toward the face.
            Transform h = bones[2];
            r._eyeLocal = Quaternion.Inverse(h.rotation) * (up * 0.09f + fwd * 0.11f);
            r._headUpLocal = Quaternion.Inverse(h.rotation) * up;
            r._headFwdLocal = Quaternion.Inverse(h.rotation) * fwd;
            r.IgnoreOwnCollisions();
            r._lastPelvis = r._parts[0].position;
            return r;
        }

        /// <summary>A single body-sized capsule standing on <paramref name="feetPhysics"/>, for a body with no rig.</summary>
        public static Ragdoll BuildCapsule(Vector3 feetPhysics, Vector3 facingVisual, Transform visual, Transform physics)
        {
            var r = new Ragdoll(1, visual, physics);
            Vector3 facing = physics.TransformDirection(visual.InverseTransformDirection(Vector3.ProjectOnPlane(facingVisual, Vector3.up)));
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.forward;
            var go = new GameObject(PartName + " capsule");
            go.layer = 2;
            go.transform.SetParent(physics, false);
            go.transform.position = feetPhysics + Vector3.up * 0.9f;
            go.transform.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
            var col = go.AddComponent<CapsuleCollider>();
            col.radius = 0.2f;
            col.height = 1.7f;
            col.direction = 1;
            col.sharedMaterial = Material();
            r._colliders.Add(col);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 2f;
            r.Configure(rb);
            r._parts[0] = rb;
            r._eyeLocal = new Vector3(0f, 0.73f, 0.06f);
            r._headUpLocal = Vector3.up;
            r.IgnoreOwnCollisions();
            r._lastPelvis = rb.position;
            return r;
        }

        private void Configure(Rigidbody rb)
        {
            rb.drag = 0.1f;
            rb.angularDrag = 0.6f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.maxAngularVelocity = 14f;
            rb.maxDepenetrationVelocity = 3f;
            rb.solverIterations = 14;
            rb.solverVelocityIterations = 6;
        }

        private GameObject Shape(int i, Transform bone, Vector3 centerVisual, Quaternion rotationVisual)
        {
            var go = new GameObject("shape");
            go.layer = 2;
            go.transform.SetParent(_parts[i].transform, false);
            go.transform.localPosition = Quaternion.Inverse(bone.rotation) * (centerVisual - bone.position);
            go.transform.localRotation = Quaternion.Inverse(bone.rotation) * rotationVisual;
            return go;
        }

        private void Box(int i, Transform bone, Vector3 centerVisual, Vector3 fwd, Vector3 up, Vector3 size)
        {
            var go = Shape(i, bone, centerVisual, Quaternion.LookRotation(Vector3.ProjectOnPlane(fwd, up).normalized, up));
            var box = go.AddComponent<BoxCollider>();
            box.size = size;
            box.sharedMaterial = Material();
            _colliders.Add(box);
        }

        private void Sphere(int i, Transform bone, Vector3 centerVisual, float radius)
        {
            var go = Shape(i, bone, centerVisual, bone.rotation);
            var s = go.AddComponent<SphereCollider>();
            s.radius = radius;
            s.sharedMaterial = Material();
            _colliders.Add(s);
        }

        /// <summary>A capsule from the bone to <paramref name="endVisual"/>, carried on past the end by <paramref name="extra"/>.</summary>
        private void Limb(int i, Transform bone, Vector3 endVisual, float radius, float extra)
        {
            Vector3 d = endVisual - bone.position;
            float length = d.magnitude;
            Vector3 dir = length > 1e-4f ? d / length : Vector3.down;
            length = Mathf.Max(length + extra, radius * 2f);
            var go = Shape(i, bone, bone.position + dir * (length * 0.5f), Quaternion.FromToRotation(Vector3.up, dir));
            var c = go.AddComponent<CapsuleCollider>();
            c.direction = 1;
            c.radius = radius;
            c.height = length + radius;
            c.sharedMaterial = Material();
            _colliders.Add(c);
        }

        private void Joint(int i, Vector3 twistVisual, Vector3 swingVisual, float lowTwist, float highTwist, float swing1, float swing2)
        {
            var rb = _parts[i];
            var j = rb.gameObject.AddComponent<CharacterJoint>();
            j.connectedBody = _parts[Parents[i]];
            j.anchor = Vector3.zero;
            // The part's rotation is its bone's, so a drawn-frame direction turned into the bone's local space is the
            // same local direction for the part.
            Quaternion toLocal = Quaternion.Inverse(Quaternion.Inverse(_physics.rotation) * rb.transform.rotation) * Quaternion.Inverse(_visual.rotation);
            j.axis = toLocal * twistVisual;
            j.swingAxis = toLocal * swingVisual;
            j.lowTwistLimit = new SoftJointLimit { limit = lowTwist };
            j.highTwistLimit = new SoftJointLimit { limit = highTwist };
            j.swing1Limit = new SoftJointLimit { limit = swing1 };
            j.swing2Limit = new SoftJointLimit { limit = swing2 };
            // Projection snaps a joint back when it is pulled apart, which keeps a fast fall together but sets a pressed
            // neck chattering, so the head does without it.
            bool neck = i == (int)RagdollPart.Head;
            j.enableProjection = !neck;
            j.projectionDistance = 0.05f;
            j.projectionAngle = 60f;
            j.enablePreprocessing = false;
            if (neck)
            {
                rb.angularDrag = 3f;
                rb.maxAngularVelocity = 7f;
            }
        }

        /// <summary>
        /// Parts never collide with each other (limbs starting inside the torso would throw the body apart), nor with the
        /// player's own controller, view or boarding colliders, which follow the fallen body round.
        /// </summary>
        private void IgnoreOwnCollisions()
        {
            for (int a = 0; a < _colliders.Count; a++)
                for (int b = a + 1; b < _colliders.Count; b++)
                    UnityEngine.Physics.IgnoreCollision(_colliders[a], _colliders[b]);
            var player = new List<Collider>();
            if (Refs.charController != null) player.AddRange(Refs.charController.GetComponentsInChildren<Collider>(true));
            if (Refs.observerMirror != null) player.AddRange(Refs.observerMirror.GetComponentsInChildren<Collider>(true));
            foreach (var e in Object.FindObjectsOfType<PlayerEmbarkerNew>()) player.AddRange(e.GetComponentsInChildren<Collider>(true));
            foreach (var e in Object.FindObjectsOfType<PlayerEmbarkerNewObserverCol>()) player.AddRange(e.GetComponentsInChildren<Collider>(true));
            foreach (var c in player)
                if (c != null)
                    foreach (var own in _colliders) UnityEngine.Physics.IgnoreCollision(own, c);
        }

        // ---- reading ----------------------------------------------------------------------------------------

        /// <summary>A point on part <paramref name="i"/> (physics world) brought into the drawn frame.</summary>
        public Vector3 ToVisual(Vector3 physicsWorld)
        {
            return _visual.TransformPoint(_physics.InverseTransformPoint(physicsWorld));
        }

        public Quaternion ToVisual(Quaternion physicsWorld)
        {
            return _visual.rotation * Quaternion.Inverse(_physics.rotation) * physicsWorld;
        }

        public Vector3 DirToPhysics(Vector3 visualDir)
        {
            return _physics.TransformDirection(_visual.InverseTransformDirection(visualDir));
        }

        /// <summary>The pelvis and every part's rotation in the drawn frame. Full ragdolls only.</summary>
        public void GetPose(out Vector3 pelvisVisual, Quaternion[] rotationsVisual)
        {
            pelvisVisual = ToVisual(_parts[0].transform.position);
            for (int i = 0; i < _parts.Length && i < rotationsVisual.Length; i++)
                rotationsVisual[i] = ToVisual(_parts[i].transform.rotation);
        }

        public Vector3 PelvisVisual { get { return ToVisual(_parts[0].transform.position); } }

        /// <summary>Where the eyes are, and which way is up for the head, in the drawn frame.</summary>
        public void GetEye(out Vector3 eyeVisual, out Vector3 headUpVisual)
        {
            var head = _parts[Full ? (int)RagdollPart.Head : 0].transform;
            Quaternion rot = ToVisual(head.rotation);
            eyeVisual = ToVisual(head.position) + rot * _eyeLocal;
            headUpVisual = rot * _headUpLocal;
        }

        /// <summary>The way the face points, in the drawn frame.</summary>
        public Vector3 HeadForwardVisual()
        {
            var head = _parts[Full ? (int)RagdollPart.Head : 0].transform;
            return ToVisual(head.rotation) * _headFwdLocal;
        }

        /// <summary>
        /// Keep a body in the sea afloat: every part under the surface is pushed up by more than its weight, harder the
        /// deeper it is, and slowed by the water, so a body that went in hard plunges, comes back up to the surface and
        /// settles there bobbing.
        /// </summary>
        public void Float(float waterVisualY, float dt)
        {
            float g = -UnityEngine.Physics.gravity.y;
            for (int i = 0; i < _parts.Length; i++)
            {
                var rb = _parts[i];
                if (rb.isKinematic) continue;
                float depth = waterVisualY - ToVisual(rb.position).y;
                if (depth <= -0.1f) continue;
                float under = Mathf.Clamp01((depth + 0.1f) / 0.35f);
                float deep = Mathf.Clamp01((depth - 0.6f) / 1.5f);
                rb.AddForce(_physics.up * (g * (1.3f * under + 1.4f * deep)), ForceMode.Acceleration);
                rb.velocity *= 1f - Mathf.Clamp01(2f * under * dt);
                rb.angularVelocity *= 1f - Mathf.Clamp01(1.5f * under * dt);
            }
        }

        /// <summary>The lowest part, less a little, in the drawn frame: about where the floor is under a fallen body.</summary>
        public float FloorVisualY()
        {
            float y = float.MaxValue;
            for (int i = 0; i < _parts.Length; i++) y = Mathf.Min(y, ToVisual(_parts[i].transform.position).y);
            return y - (Full ? 0.08f : 0.2f);
        }

        public bool IsStill()
        {
            for (int i = 0; i < _parts.Length; i++)
            {
                if (_parts[i].velocity.sqrMagnitude > 0.06f || _parts[i].angularVelocity.sqrMagnitude > 0.5f) return false;
            }
            return true;
        }

        public Vector3 PelvisVelocity { get { return _parts[0].velocity; } }

        public bool Kinematic { get { return _parts[0].isKinematic; } }

        // ---- acting on it -----------------------------------------------------------------------------------

        /// <summary>
        /// Push the body over (physics world, m/s). The upper body takes the most of it and the feet hardly any, so it
        /// topples from the feet rather than skating across the deck standing up.
        /// </summary>
        public void Push(Vector3 velocity)
        {
            if (!Full)
            {
                var rb = _parts[0];
                Vector3 dir = velocity.normalized;
                float strength = Mathf.Clamp(velocity.magnitude, 0.5f, 6f);
                rb.velocity += dir * (strength * 0.35f) + Vector3.up * 0.3f;
                rb.angularVelocity += Vector3.Cross(Vector3.up, dir) * (strength * 0.9f) + Vector3.up * Random.Range(-1.2f, 1.2f);
                return;
            }
            float feet = float.MaxValue, top = float.MinValue;
            for (int i = 0; i < Count; i++)
            {
                float y = _parts[i].position.y;
                feet = Mathf.Min(feet, y);
                top = Mathf.Max(top, y);
            }
            float span = Mathf.Max(0.5f, top - feet);
            for (int i = 0; i < Count; i++)
            {
                float k = Mathf.Clamp01((_parts[i].position.y - feet) / span);
                _parts[i].velocity += velocity * (0.15f + 0.85f * k) + Vector3.up * 0.25f;
            }
            _parts[(int)RagdollPart.Chest].angularVelocity += Vector3.up * Random.Range(-1.5f, 1.5f);
        }

        /// <summary>Carry on at the speed the player was already moving (physics world, m/s), for a body that fell rather than was pushed.</summary>
        public void SetVelocity(Vector3 velocity)
        {
            for (int i = 0; i < _parts.Length; i++) _parts[i].velocity = velocity;
        }

        public void SetKinematic(bool on)
        {
            for (int i = 0; i < _parts.Length; i++)
            {
                if (!on) _parts[i].isKinematic = false;
                else
                {
                    _parts[i].velocity = Vector3.zero;
                    _parts[i].angularVelocity = Vector3.zero;
                    _parts[i].isKinematic = true;
                }
            }
        }

        /// <summary>
        /// Move every part to a new pair of frames, each where it was on screen, velocities turned to match. The game
        /// does this to the player when they leave a boat or climb onto one.
        /// </summary>
        public void MoveToFrames(Transform visual, Transform physics)
        {
            if (visual == null || physics == null) return;
            for (int i = 0; i < _parts.Length; i++)
            {
                var rb = _parts[i];
                var t = rb.transform;
                Vector3 pos = ToVisual(t.position);
                Quaternion rot = ToVisual(t.rotation);
                Vector3 vel = _visual.TransformDirection(_physics.InverseTransformDirection(rb.velocity));
                Vector3 ang = _visual.TransformDirection(_physics.InverseTransformDirection(rb.angularVelocity));
                t.SetParent(physics, true);
                t.position = physics.TransformPoint(visual.InverseTransformPoint(pos));
                t.rotation = physics.rotation * Quaternion.Inverse(visual.rotation) * rot;
                if (!rb.isKinematic)
                {
                    rb.velocity = physics.TransformDirection(visual.InverseTransformDirection(vel));
                    rb.angularVelocity = physics.TransformDirection(visual.InverseTransformDirection(ang));
                }
            }
            _visual = visual;
            _physics = physics;
            _lastPelvis = _parts[0].position;
        }

        /// <summary>
        /// A backstop for continuous collision, run each physics step: if the pelvis went through a solid surface since
        /// the last step, the whole body goes back on top of it with its speed into the surface taken away.
        /// </summary>
        public void CatchTunneling()
        {
            var pelvis = _parts[0];
            Vector3 now = pelvis.position;
            Vector3 moved = now - _lastPelvis;
            float dist = moved.magnitude;
            if (dist > 0.15f && !pelvis.isKinematic)
            {
                RaycastHit hit;
                if (CastSolid(_lastPelvis, moved / dist, dist, out hit))
                {
                    Vector3 shift = hit.point + hit.normal * 0.15f - now;
                    for (int i = 0; i < _parts.Length; i++)
                    {
                        var rb = _parts[i];
                        rb.transform.position += shift;
                        float into = Vector3.Dot(rb.velocity, hit.normal);
                        if (into < 0f) rb.velocity -= hit.normal * into;
                    }
                    Plugin.Log.LogInfo($"[Downed] caught the body going through {hit.collider.name} at {dist:F2} m in a step");
                    now = pelvis.transform.position;
                }
            }
            _lastPelvis = now;
        }

        private static readonly RaycastHit[] _hits = new RaycastHit[16];

        private bool CastSolid(Vector3 from, Vector3 dir, float dist, out RaycastHit best)
        {
            best = default(RaycastHit);
            int n = UnityEngine.Physics.RaycastNonAlloc(from, dir, _hits, dist, Seating.SeatLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || Downed.IgnoredForRoom(c)) continue;
                if (_hits[i].distance < nearest) { nearest = _hits[i].distance; best = _hits[i]; }
            }
            return best.collider != null;
        }

        public void Destroy()
        {
            for (int i = 0; i < _parts.Length; i++)
                if (_parts[i] != null) Object.Destroy(_parts[i].gameObject);
        }

        /// <summary>True for any collider that belongs to a ragdoll part.</summary>
        public static bool IsPart(Collider c)
        {
            var rb = c.attachedRigidbody;
            return rb != null && rb.gameObject.name.StartsWith(PartName);
        }

        private static PhysicMaterial _material;

        /// <summary>Grippy and dead, so a body tips over its feet and lies where it lands instead of bouncing.</summary>
        private static PhysicMaterial Material()
        {
            if (_material != null) return _material;
            _material = new PhysicMaterial("PlayerModelRagdoll")
            {
                dynamicFriction = 0.7f,
                staticFriction = 0.9f,
                bounciness = 0.02f,
                frictionCombine = PhysicMaterialCombine.Maximum,
                bounceCombine = PhysicMaterialCombine.Minimum,
            };
            return _material;
        }
    }
}
