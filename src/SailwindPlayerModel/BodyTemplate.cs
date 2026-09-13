using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The one humanoid template every body clones from, and the small transform helpers that go with it.
    ///
    /// THERE IS NO ART TO SHIP. Sailwind's port NPCs are Synty modular characters, so the template is a live
    /// NPC found in the loaded scenes, cloned, stripped of everything that makes it an NPC, pinned to the
    /// world scale it had in-scene, and cached across scene unloads.
    ///
    /// The harbormaster (PortDude) is preferred because he lives in the persistent world scene ('the ocean'),
    /// so he is there from the moment the world loads, at sea included. A shopkeeper is the fallback; those
    /// stream in and out with the island scenery. Until the world has loaded there is no template, and
    /// callers get false and retry later rather than an exception.
    /// </summary>
    public static class BodyTemplate
    {
        private static GameObject _template;

        /// <summary>The cached stripped template, or null if no usable NPC has been found yet.</summary>
        public static GameObject Instance { get { return Ensure() ? _template : null; } }

        /// <summary>True once a body can be built. False until the world has loaded.</summary>
        public static bool Available { get { return Ensure(); } }

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

            var template = Object.Instantiate(src);
            // PortDude.Awake calls port.RegisterDude(this), and Instantiate runs Awake at once on an active
            // source, so the clone has just made itself the port's harbormaster. Hand the job back to the real
            // one before the clone is stripped, or the mission table ends up pointing at a dead component.
            foreach (var dude in src.GetComponentsInChildren<PortDude>(true))
            {
                var port = dude.GetPort();
                if (port != null) port.RegisterDude(dude);
            }
            template.name = "PlayerModelBodyTemplate";
            // Match the size the NPC had IN-SCENE. Instantiate copies src's LOCAL scale, but the NPC sits under
            // scaled parents, so its real on-screen size is the WORLD (lossy) scale. A parentless clone with
            // only the local scale renders about twice too big; pin it to the captured world scale.
            template.transform.localScale = src.transform.lossyScale;
            // Deactivate BEFORE stripping: prevents any deferred Start (which NREs on a parentless clone) from
            // being scheduled, and guarantees no active duplicate is rendered.
            template.SetActive(false);
            Strip(template);
            Object.DontDestroyOnLoad(template);
            _template = template;
            Plugin.Log.LogInfo($"[PlayerModel] Captured a humanoid body template from {kind} '{src.name}' (scene '{src.scene.name}')");
            return true;
        }

        /// <summary>
        /// The character root of the first usable NPC of this kind, or null. "Usable" means a live skinned
        /// body under it; among those, one carrying the CharacterCustomizer wins, so the character screen has
        /// parts to switch. The NPC script may sit on a trigger child rather than the character itself, so the
        /// root is found by walking up to the nearest object that owns the customizer.
        /// </summary>
        private static GameObject PickSource<T>() where T : Component
        {
            GameObject plain = null;
            foreach (var c in Resources.FindObjectsOfTypeAll<T>())
            {
                if (c == null) continue;
                var go = c.gameObject;
                if (!go.scene.IsValid()) continue;      // skip prefab assets
                if (go.hideFlags != HideFlags.None) continue;

                var root = FindCharacterRoot(go);
                if (root != null) return root;          // customizable: take it

                if (plain == null && HasLiveSkinnedBody(go)) plain = go;
            }
            return plain;
        }

        /// <summary>Nearest ancestor-or-self that owns a CharacterCustomizer and has a live skinned body.</summary>
        private static GameObject FindCharacterRoot(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (t.GetComponent<PsychoticLab.CharacterCustomizer>() == null) continue;
                return HasLiveSkinnedBody(t.gameObject) ? t.gameObject : null;
            }
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

        /// <summary>
        /// Remove gameplay, physics and proxy components so the clone is a pure visual.
        ///
        /// The Animator goes too, which is why every pose in this mod is procedural bone writing: there is no
        /// Animator and no clips anywhere in a body built from this template. That is deliberate - it has a
        /// null controller (inert) but applyRootMotion=true, so leaving it would give something a way to fight
        /// the pose - and it is also what makes a slump or a fall the same kind of operation the walk is.
        /// </summary>
        public static void Strip(GameObject root)
        {
            // Disable BEFORE Destroy (Destroy is deferred to end of frame; disabling takes effect
            // immediately) so the shopkeeper brain and its trade trigger can never fire, even on this frame.
            foreach (var c in root.GetComponentsInChildren<Shopkeeper>(true)) { c.enabled = false; Object.Destroy(c); }
            foreach (var c in root.GetComponentsInChildren<PortDude>(true)) { c.enabled = false; Object.Destroy(c); }
            foreach (var c in root.GetComponentsInChildren<NPCPlayerCol>(true)) Object.Destroy(c);
            foreach (var c in root.GetComponentsInChildren<NPCAnimations>(true)) Object.Destroy(c);
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) { c.enabled = false; Object.Destroy(c); }
            foreach (var c in root.GetComponentsInChildren<Rigidbody>(true)) Object.Destroy(c);
            foreach (var c in root.GetComponentsInChildren<Animator>(true)) Object.Destroy(c);
            // The shopkeeper root carries a stray proxy MeshRenderer/MeshFilter - drop it (the body is skinned).
            var mr = root.GetComponent<MeshRenderer>(); if (mr != null) Object.Destroy(mr);
            var mf = root.GetComponent<MeshFilter>(); if (mf != null) Object.Destroy(mf);
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
