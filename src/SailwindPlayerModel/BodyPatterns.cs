using System;
using System.Collections.Generic;
using PsychoticLab;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Keeps clothing patterns on the clothes while the body moves.
    ///
    /// The patterned shader draws a pattern from a position (the game's UVs only point at palette cells), and the
    /// position has to be the REST pose, or a sleeve would slide through its stripes as the arm swings. The character
    /// meshes are not readable on the CPU (all 719 of them, checked in the game's assets), so rest positions cannot be
    /// baked into the meshes. Instead every worn part follows ONE bone: each frame, before any camera renders, the part's
    /// renderer gets a matrix (_PatSpace) that takes a world position into that bone's space and from there to where it
    /// sat on the untouched body template, standing at rest, in body heights. Rigid gear and the split limb pieces
    /// (upper arm, lower arm, hand) follow their bone exactly; a leg, one mesh over the knee, follows the thigh, so its
    /// shin drifts a little when the knee bends.
    ///
    /// Only bodies whose look has a pattern are tracked (PlayerAppearance.ApplyPatternShader), so a crew without
    /// patterns costs nothing here. The matrix goes into each renderer's property block next to the part colors that
    /// PlayerAppearance writes there; this reads the block back and adds to it, so neither overwrites the other for
    /// more than the frame in which the appearance itself changed.
    /// </summary>
    internal static class BodyPatterns
    {
        private sealed class Part
        {
            public Renderer Renderer;
            public Transform Bone;
            public Matrix4x4 RestFromBone;   // normalization * (template root <- template bone)
            public Vector4 Range;            // the part's rest height range, for Fade and Quartered
            public Matrix4x4 Last;           // the matrix last written into the renderer's block
            public bool HasLast;
        }

        private sealed class Body
        {
            public CharacterCustomizer Customizer;
            public bool Dirty = true;
            // The colors were applied again, which rewrites or clears the renderers' property blocks, so every part's
            // matrix goes back in even where it has not moved.
            public bool Reapply = true;
            public int PartsSeen;
            public int SigFrame = -1;
            public readonly List<Part> Parts = new List<Part>();
        }

        private static readonly Dictionary<int, Body> _bodies = new Dictionary<int, Body>();
        private static readonly List<int> _dead = new List<int>();
        private static MaterialPropertyBlock _block;
        private static int _space = -1, _range = -1;
        private static bool _hooked;
        private static readonly HashSet<int> _failedLogged = new HashSet<int>();   // bodies whose failure was logged

        /// <summary>
        /// Follow this body's parts. Called whenever its appearance is applied with a pattern, which can be every frame
        /// while a color slider is dragged: so this only asks for the matrices to be written again. The parts are found
        /// again only for a new body or when what it wears changes (Signature), not for a recolor.
        /// </summary>
        public static void Track(CharacterCustomizer c)
        {
            if (c == null) return;
            Body b;
            int id = c.GetInstanceID();
            if (!_bodies.TryGetValue(id, out b)) _bodies[id] = b = new Body { Customizer = c };
            b.Reapply = true;
            if (!_hooked)
            {
                _hooked = true;
                Camera.onPreCull += OnPreCull;
            }
        }

        public static void Untrack(CharacterCustomizer c)
        {
            if (c != null) _bodies.Remove(c.GetInstanceID());
        }

        /// <summary>
        /// Before every camera culls, not once per frame: the character screen's preview camera renders by hand inside a
        /// LateUpdate, which can come before the body's own pose code, so the first camera of a frame may see bones the
        /// main camera will not. Each part's matrix is worked out per camera, which is cheap, but written into its
        /// renderer's property block only when it changed or the colors were applied again, so a frame's later cameras
        /// (reflections, water) cost a comparison per part. What a body wears is checked once per frame.
        ///
        /// One body failing does not stop the others: each is placed on its own, and its first failure is logged.
        /// </summary>
        private static void OnPreCull(Camera cam)
        {
            if (_bodies.Count == 0) return;
            if (_block == null)
            {
                _block = new MaterialPropertyBlock();
                _space = Shader.PropertyToID("_PatSpace");
                _range = Shader.PropertyToID("_PatRange");
            }
            int frame = Time.frameCount;
            _dead.Clear();
            foreach (var kv in _bodies)
            {
                var b = kv.Value;
                try
                {
                    if (b.Customizer == null) { _dead.Add(kv.Key); continue; }
                    // UpdateModel rebuilds enabledObjects. Its members, not its count: one shirt swapped for another
                    // keeps the count, and a body's first build can too.
                    if (b.Dirty || b.SigFrame != frame)
                    {
                        b.SigFrame = frame;
                        int seen = Signature(b.Customizer.enabledObjects);
                        if (b.Dirty || seen != b.PartsSeen) Rebuild(b, seen);
                    }
                    bool all = b.Reapply;
                    b.Reapply = false;
                    foreach (var p in b.Parts)
                    {
                        if (p.Renderer == null || p.Bone == null || !p.Renderer.enabled) continue;
                        var m = p.RestFromBone * p.Bone.worldToLocalMatrix;
                        if (!all && p.HasLast && m == p.Last) continue;
                        p.Renderer.GetPropertyBlock(_block);
                        _block.SetMatrix(_space, m);
                        _block.SetVector(_range, p.Range);
                        p.Renderer.SetPropertyBlock(_block);
                        p.Last = m;
                        p.HasLast = true;
                    }
                }
                catch (Exception e)
                {
                    // Cosmetic. A failure leaves this body's patterns where its last good frame put them.
                    b.Reapply = true;
                    if (_failedLogged.Add(kv.Key))
                        Plugin.Log.LogWarning("[Patterns] Could not place the patterns on a body: " + e);
                }
            }
            foreach (var id in _dead) { _bodies.Remove(id); _failedLogged.Remove(id); }
        }

        private static int Signature(List<GameObject> worn)
        {
            if (worn == null) return 0;
            unchecked
            {
                int h = 17;
                foreach (var go in worn) h = h * 31 + (go != null ? go.GetInstanceID() : 0);
                return h;
            }
        }

        // ---- which bone each part follows -----------------------------------------------------------------------

        private static readonly string[] Spine = { "Spine_03", "Spine_02", "Spine_01", "Hips" };
        private static readonly string[] Hips = { "Hips", "Spine_01" };
        private static readonly string[] Head = { "Head", "Neck", "Spine_03", "Spine_02" };
        private static string[] One(string a, string b) { return new[] { a, b, "Spine_01" }; }

        /// <summary>
        /// The part lists of the gender worn and the shared accessories, each with the bones it follows (first found
        /// wins) and the row it belongs to (parts of one row share a height range, so a Fade runs over the whole row).
        /// </summary>
        private static IEnumerable<KeyValuePair<List<GameObject>, KeyValuePair<string[], string>>> Lists(CharacterCustomizer c)
        {
            var g = c.isFemale ? c.female : c.male;
            var a = c.allGender;
            var all = new List<KeyValuePair<List<GameObject>, KeyValuePair<string[], string>>>();
            Action<List<GameObject>, string[], string> add = (list, bones, row) =>
            {
                if (list != null) all.Add(new KeyValuePair<List<GameObject>, KeyValuePair<string[], string>>(list, new KeyValuePair<string[], string>(bones, row)));
            };
            if (g != null)
            {
                add(g.headAllElements, Head, "head"); add(g.headNoElements, Head, "head");
                add(g.eyebrow, Head, "head"); add(g.facialHair, Head, "head");
                add(g.torso, Spine, "torso");
                add(g.arm_Upper_Right, One("Shoulder_R", "Clavicle_R"), "torso");
                add(g.arm_Upper_Left, One("Shoulder_L", "Clavicle_L"), "torso");
                add(g.arm_Lower_Right, One("Elbow_R", "Shoulder_R"), "torso");
                add(g.arm_Lower_Left, One("Elbow_L", "Shoulder_L"), "torso");
                add(g.hand_Right, One("Hand_R", "Elbow_R"), "gloves");
                add(g.hand_Left, One("Hand_L", "Elbow_L"), "gloves");
                add(g.hips, Hips, "hips");
                add(g.leg_Right, One("UpperLeg_R", "Hips"), "legs");
                add(g.leg_Left, One("UpperLeg_L", "Hips"), "legs");
            }
            if (a != null)
            {
                add(a.all_Hair, Head, "head"); add(a.all_Head_Attachment, Head, "head");
                add(a.headCoverings_Base_Hair, Head, "headgear");
                add(a.headCoverings_No_FacialHair, Head, "face");
                add(a.headCoverings_No_Hair, Head, "hood");
                add(a.chest_Attachment, Spine, "torso");
                add(a.back_Attachment, Spine, "back");
                add(a.shoulder_Attachment_Right, One("Shoulder_R", "Clavicle_R"), "shoulders");
                add(a.shoulder_Attachment_Left, One("Shoulder_L", "Clavicle_L"), "shoulders");
                add(a.elbow_Attachment_Right, One("Elbow_R", "Shoulder_R"), "elbows");
                add(a.elbow_Attachment_Left, One("Elbow_L", "Shoulder_L"), "elbows");
                add(a.hips_Attachment, Hips, "belt");
                add(a.knee_Attachement_Right, One("LowerLeg_R", "UpperLeg_R"), "knees");
                add(a.knee_Attachement_Left, One("LowerLeg_L", "UpperLeg_L"), "knees");
                add(a.elf_Ear, Head, "head");
            }
            return all;
        }

        private static void Rebuild(Body b, int seen)
        {
            b.Dirty = false;
            b.PartsSeen = seen;
            b.Parts.Clear();
            var c = b.Customizer;
            Matrix4x4 norm;
            if (!Rest.TryNormalization(out norm)) { b.Dirty = true; return; }   // no template yet: try next frame

            var root = c.transform;
            var worn = new HashSet<GameObject>();
            if (c.enabledObjects != null) foreach (var go in c.enabledObjects) if (go != null) worn.Add(go);

            var rows = new Dictionary<string, Vector2>();
            var pending = new List<KeyValuePair<Part, string>>();
            foreach (var entry in Lists(c))
            {
                string[] bones = entry.Value.Key;
                string row = entry.Value.Value;
                foreach (var go in entry.Key)
                {
                    if (go == null || !worn.Contains(go)) continue;
                    var r = go.GetComponent<SkinnedMeshRenderer>();
                    if (r == null || r.sharedMesh == null) continue;
                    Transform bone = null;
                    Matrix4x4 rest = Matrix4x4.identity;
                    foreach (var name in bones)
                    {
                        bone = BodyTemplate.FindDeep(root, name);
                        if (bone != null && Rest.TryBone(name, out rest)) break;
                        bone = null;
                    }
                    if (bone == null) continue;

                    // The part's rest height range: its mesh bounds, from the renderer's own (unanimated) place under the
                    // root into body heights. A mesh sits in its renderer's space at bind time.
                    var toRoot = norm * root.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    var bounds = r.sharedMesh.bounds;
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = bounds.center + Vector3.Scale(bounds.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        float y = toRoot.MultiplyPoint3x4(corner).y;
                        lo = Mathf.Min(lo, y);
                        hi = Mathf.Max(hi, y);
                    }
                    Vector2 have;
                    rows[row] = rows.TryGetValue(row, out have) ? new Vector2(Mathf.Min(have.x, lo), Mathf.Max(have.y, hi)) : new Vector2(lo, hi);

                    var part = new Part { Renderer = r, Bone = bone, RestFromBone = norm * rest };
                    pending.Add(new KeyValuePair<Part, string>(part, row));
                }
            }
            foreach (var kv in pending)
            {
                var range = rows[kv.Value];
                kv.Key.Range = new Vector4(range.x, range.y, 0, 1);   // w = placed; see BodyPattern.cginc
                b.Parts.Add(kv.Key);
            }
        }

        // ---- the rest pose, from the body template --------------------------------------------------------------

        /// <summary>
        /// Rest-pose matrices read once from the body template, which is never animated: it is the clone every body is
        /// made from, kept inactive. All in the template root's own space, which is the same space under every body's
        /// root, since each body is a copy of it.
        /// </summary>
        private static class Rest
        {
            private static readonly Dictionary<string, Matrix4x4> _bones = new Dictionary<string, Matrix4x4>();
            private static GameObject _from;
            private static Matrix4x4 _norm;
            private static bool _haveNorm;

            private static bool Fresh()
            {
                var t = BodyTemplate.Instance;
                if (t == null) return false;
                if (t != _from)
                {
                    _from = t;
                    _bones.Clear();
                    _haveNorm = false;
                }
                return true;
            }

            /// <summary>Template root space for a point in the named bone's space, at rest.</summary>
            public static bool TryBone(string name, out Matrix4x4 m)
            {
                m = Matrix4x4.identity;
                if (!Fresh()) return false;
                if (_bones.TryGetValue(name, out m)) return true;
                var root = _from.transform;
                var bone = BodyTemplate.FindDeep(root, name);
                if (bone == null) return false;
                m = root.worldToLocalMatrix * bone.localToWorldMatrix;
                _bones[name] = m;
                return true;
            }

            /// <summary>
            /// Template root space to body heights: the ankles at 0 and the head bone at 1, centered on the hips, so the
            /// shader's pattern sizes hold at any character scale.
            /// </summary>
            public static bool TryNormalization(out Matrix4x4 m)
            {
                m = Matrix4x4.identity;
                if (!Fresh()) return false;
                if (_haveNorm) { m = _norm; return true; }
                Matrix4x4 head, ankleL, ankleR, hips;
                if (!TryBone("Head", out head) || !TryBone("Ankle_L", out ankleL) || !TryBone("Ankle_R", out ankleR) || !TryBone("Hips", out hips))
                    return false;
                float foot = (ankleL.GetColumn(3).y + ankleR.GetColumn(3).y) * 0.5f;
                float height = head.GetColumn(3).y - foot;
                if (height < 1e-4f) return false;
                var center = hips.GetColumn(3);
                _norm = Matrix4x4.Scale(Vector3.one / height) * Matrix4x4.Translate(new Vector3(-center.x, -foot, -center.z));
                _haveNorm = true;
                Plugin.Log.LogInfo($"[Patterns] rest pose read from the template: height {height:F3} (template units), feet at {foot:F3}");
                m = _norm;
                return true;
            }
        }
    }
}
