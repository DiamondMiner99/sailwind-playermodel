using System;
using System.Collections.Generic;
using HarmonyLib;
using PsychoticLab;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The one humanoid template every body clones from, and the small transform helpers that go with it.
    ///
    /// THERE IS NO ART TO SHIP. Sailwind's port NPCs are Synty modular characters, so the template is a live
    /// NPC found in the loaded scenes, cloned, stripped down to a pure visual, pinned to the world scale it
    /// had in-scene, and cached across scene unloads.
    ///
    /// The harbormaster (PortDude) is preferred because he lives in the persistent world scene ('the ocean'),
    /// so he is there from the moment the world loads, at sea included. A shopkeeper is the fallback; those
    /// stream in and out with the island scenery. Until the world has loaded there is no template, and
    /// callers get false and retry later rather than an exception.
    ///
    /// WHAT GETS CLONED IS THE CHARACTER, NOT THE NPC. Both NPC kinds are laid out as a top-level object that
    /// carries the NPC script, colliders and a rigidbody, with the Synty character ("Modular NPC", which owns
    /// the CharacterCustomizer) as ONE of its children. The harbormaster's top-level object also holds his
    /// mission table, book and money chest. v0.1.1 cloned that top-level object and players got the whole
    /// stall strapped to their back, so the template is now the customizer's own object and nothing above it.
    /// </summary>
    public static class BodyTemplate
    {
        private static GameObject _template;

        /// <summary>The cached stripped template, or null if no usable NPC has been found yet.</summary>
        public static GameObject Instance { get { return Ensure() ? _template : null; } }

        /// <summary>True once a body can be built. False until the world has loaded.</summary>
        public static bool Available { get { return Ensure(); } }

        /// <summary>
        /// The template's own copy of the cloned NPC's material: the "as cloned" palette every body starts
        /// from. Bodies take a private copy of it before they write colors, so neither the live NPC nor
        /// another body is ever recolored. Null until a template exists, or if the NPC had no material.
        /// </summary>
        public static Material SharedMaterial { get; private set; }

        /// <summary>Capture and cache (once) a stripped, inactive humanoid template that survives scene unloads.</summary>
        public static bool Ensure()
        {
            if (_template != null) return true;

            // Prefer the harbormaster (PortDude, the mission-table NPC). Port objects never unload, since every
            // port's market and missions keep ticking wherever the player is, so he is available far out at
            // sea where a shopkeeper, which streams with the island scenery, is not. (NANDBrew, 2026-09-12.)
            string kind = "PortDude";
            GameObject src = PickSource<PortDude>();
            if (src == null) { kind = "Shopkeeper"; src = PickSource<Shopkeeper>(); }
            if (src == null) return false;

            var template = UnityEngine.Object.Instantiate(src);
            // PortDude.Awake calls port.RegisterDude(this), and Instantiate runs Awake at once on an active
            // source. The character object normally carries no PortDude (it sits on the parent), but if a
            // layout ever puts them together, hand the job back to the real one before the clone is stripped.
            foreach (var dude in src.GetComponentsInChildren<PortDude>(true))
            {
                var port = dude.GetPort();
                if (port != null) port.RegisterDude(dude);
            }
            template.name = "PlayerModelBodyTemplate";
            // Match the size the NPC had IN-SCENE. Instantiate copies src's LOCAL scale, but the character sits
            // under scaled parents, so its real on-screen size is the WORLD (lossy) scale. A parentless clone
            // with only the local scale renders about twice too small or too big; pin it to the world scale.
            template.transform.localScale = src.transform.lossyScale;
            // The part lists are what the appearance code counts against. They are built by vanilla's Start
            // and copied by Instantiate, so they are normally populated already; rebuilding them here makes
            // that true regardless of what state the source was in. Must run while the clone is still ACTIVE:
            // vanilla's BuildLists only sees active objects.
            RebuildPartLists(template);
            // The customizer's material is the SOURCE NPC's own instance, shared by reference with him. Take
            // our own copy so nothing this mod writes can touch a live NPC.
            var cust = template.GetComponentInChildren<CharacterCustomizer>(true);
            if (cust != null && cust.mat != null)
            {
                cust.mat = new Material(cust.mat) { name = cust.mat.name + " (player model)" };
                SharedMaterial = cust.mat;
            }
            // Deactivate BEFORE stripping: prevents any deferred Start (which NREs on a parentless clone) from
            // being scheduled, and guarantees no active duplicate is rendered.
            template.SetActive(false);
            Strip(template);
            UnityEngine.Object.DontDestroyOnLoad(template);
            _template = template;

            string parent = src.transform.parent != null ? src.transform.parent.name : "(none)";
            Plugin.Log.LogInfo($"[PlayerModel] Captured a humanoid body template from {kind} '{src.name}' under '{parent}' " +
                               $"(scene '{src.scene.name}', world scale {src.transform.lossyScale.x:F2}). {DescribeParts(template)}");
            return true;
        }

        /// <summary>
        /// The character root of the best usable NPC of this kind, or null. The root is the object that OWNS
        /// the CharacterCustomizer: found by walking up from the NPC script's object, then by searching down
        /// into its children, which is where both the harbormaster and the shopkeepers keep it. Only NPCs
        /// that are active in the hierarchy qualify: an inactive one never ran its Start, so its part lists
        /// are empty and it cannot be dressed. Among the candidates, a real port's harbormaster at the
        /// standard size wins, and leftover objects with "test" in the name lose. Ties go to the lowest
        /// parent-and-name string, so every machine on the same game build clones the SAME NPC and wears
        /// the same palette: each NPC carries its own material colors, and FindObjectsOfTypeAll's order is
        /// not the same from one machine to the next.
        /// </summary>
        private static GameObject PickSource<T>() where T : Component
        {
            GameObject best = null;
            int bestScore = int.MinValue;
            string bestKey = null;
            foreach (var c in Resources.FindObjectsOfTypeAll<T>())
            {
                if (c == null) continue;
                var go = c.gameObject;
                if (!go.scene.IsValid()) continue;      // skip prefab assets
                if (go.hideFlags != HideFlags.None) continue;
                if (!go.activeInHierarchy) continue;

                var root = FindCharacterRoot(go);
                if (root == null) continue;

                int score = 0;
                if (go.name.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0) score -= 10;
                var ls = root.transform.localScale;
                if (Mathf.Abs(ls.x - 1f) < 0.01f && Mathf.Abs(ls.y - 1f) < 0.01f && Mathf.Abs(ls.z - 1f) < 0.01f) score += 1;
                var dude = c as PortDude;
                if (dude != null && dude.GetPort() != null) score += 2;

                string key = (go.transform.parent != null ? go.transform.parent.name : "") + "/" + go.name;
                if (score > bestScore || (score == bestScore && string.CompareOrdinal(key, bestKey) < 0))
                {
                    best = root; bestScore = score; bestKey = key;
                }
            }
            return best;
        }

        /// <summary>
        /// The object that owns the CharacterCustomizer and has a live skinned body under it: the nearest
        /// ancestor-or-self first, else the first one among the descendants. Null if there is none.
        /// </summary>
        private static GameObject FindCharacterRoot(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (t.GetComponent<CharacterCustomizer>() == null) continue;
                return HasLiveSkinnedBody(t.gameObject) ? t.gameObject : null;
            }
            var c = go.GetComponentInChildren<CharacterCustomizer>(true);
            if (c != null && HasLiveSkinnedBody(c.gameObject)) return c.gameObject;
            return null;
        }

        private static bool HasLiveSkinnedBody(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                if (t.name.ToLower().Contains("combiner")) return false; // AlAnkh baked static NPC
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.enabled && smr.sharedMesh != null) return true;
            return false;
        }

        /// <summary>Run vanilla's BuildLists on the clone's customizer. Never throws; a failure is logged.</summary>
        private static void RebuildPartLists(GameObject root)
        {
            var c = root.GetComponentInChildren<CharacterCustomizer>(true);
            if (c == null) { Plugin.Log.LogWarning("[PlayerModel] Template has no CharacterCustomizer; it cannot be dressed"); return; }
            try { Traverse.Create(c).Method("BuildLists").GetValue(); }
            catch (Exception e) { Plugin.Log.LogWarning("[PlayerModel] BuildLists on the template failed: " + e.Message); }
        }

        /// <summary>Part-library sizes on a clone, for the capture log. Tells us what a reporter's NPC has.</summary>
        public static string DescribeParts(GameObject root)
        {
            var c = root != null ? root.GetComponentInChildren<CharacterCustomizer>(true) : null;
            if (c == null) return "no customizer";
            int Count(List<GameObject> l) { return l != null ? l.Count : 0; }
            var m = c.male; var f = c.female; var a = c.allGender;
            return $"parts: male head={(m != null ? Count(m.headAllElements) : 0)} torso={(m != null ? Count(m.torso) : 0)} " +
                   $"hips={(m != null ? Count(m.hips) : 0)} legs={(m != null ? Count(m.leg_Right) : 0)} " +
                   $"facialhair={(m != null ? Count(m.facialHair) : 0)}, female torso={(f != null ? Count(f.torso) : 0)}, " +
                   $"hair={(a != null ? Count(a.all_Hair) : 0)} hats={(a != null ? Count(a.headCoverings_Base_Hair) : 0)}";
        }

        /// <summary>
        /// Reduce the clone to a pure visual: only transforms, skinned meshes and the CharacterCustomizer
        /// survive. Everything else, known or not, is removed: the Animator (which has a null controller but
        /// applyRootMotion=true, so leaving it would give something a way to fight the pose), NPC brains,
        /// colliders, the rigidbody, and whatever a future NPC prefab hangs on the character. That is why
        /// every pose in this mod is procedural bone writing: there is no Animator and no clips anywhere in a
        /// body built from this template, and a slump or a fall is the same kind of operation the walk is.
        ///
        /// Removal is immediate, not deferred, so the template is clean the moment this returns. Scripts go
        /// first so that anything they RequireComponent (Animator, Rigidbody) can go in the second pass.
        /// </summary>
        public static void Strip(GameObject root)
        {
            // Disable first: takes effect at once, so a brain or trigger can never fire, whatever happens below.
            foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                if (!(b is CharacterCustomizer)) b.enabled = false;
            foreach (var col in root.GetComponentsInChildren<Collider>(true)) col.enabled = false;

            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && !(mb is CharacterCustomizer)) Remove(mb);
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && !(c is Transform) && !(c is SkinnedMeshRenderer) && !(c is CharacterCustomizer)) Remove(c);

            var left = new List<string>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && !(c is Transform) && !(c is SkinnedMeshRenderer) && !(c is CharacterCustomizer))
                    left.Add(c.GetType().Name + " on " + c.gameObject.name);
            if (left.Count > 0)
                Plugin.Log.LogWarning("[PlayerModel] Template still carries: " + string.Join(", ", left.ToArray()));
        }

        private static void Remove(Component c)
        {
            try { UnityEngine.Object.DestroyImmediate(c); }
            catch (Exception e) { Plugin.Log.LogWarning($"[PlayerModel] Could not remove {c.GetType().Name} from the template: {e.Message}"); }
        }

        public static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        /// <summary>Depth-first search for a bone by exact name.</summary>
        public static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>Reset a bone to its captured rest rotation, then swing it. The basic pose operation here.</summary>
        public static void SetSwing(Transform t, Quaternion bind, Vector3 localAxis, float degrees)
        {
            if (t == null) return;
            t.localRotation = bind;
            t.Rotate(localAxis, degrees, Space.Self);
        }
    }
}
