using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The one humanoid template every body clones from, and the small transform helpers that go with it.
    ///
    /// THERE IS NO ART TO SHIP. Sailwind's shopkeepers are Synty modular NPCs, so the template is a live
    /// shopkeeper found in the loaded scene, cloned, stripped of everything that makes it a merchant, pinned
    /// to the world scale it had in-scene, and cached across scene unloads.
    ///
    /// The catch that every consumer has to handle: the template is NULL until a shopkeeper has loaded at
    /// least once this session. Load a save at sea and never dock, and there is no body to clone. Nothing here
    /// throws in that case - it returns false and the caller retries later.
    /// </summary>
    public static class BodyTemplate
    {
        private static GameObject _template;

        /// <summary>The cached stripped template, or null if no shopkeeper has been seen yet this session.</summary>
        public static GameObject Instance { get { return Ensure() ? _template : null; } }

        /// <summary>True once a body can be built. False out at sea before a first docking.</summary>
        public static bool Available { get { return Ensure(); } }

        /// <summary>Capture and cache (once) a stripped, inactive humanoid template that survives scene unloads.</summary>
        public static bool Ensure()
        {
            if (_template != null) return true;

            GameObject src = null;
            foreach (var sk in Resources.FindObjectsOfTypeAll<Shopkeeper>())
            {
                if (sk == null) continue;
                var go = sk.gameObject;
                if (!go.scene.IsValid()) continue;      // skip prefab assets
                if (go.hideFlags != HideFlags.None) continue;
                if (!HasLiveSkinnedBody(go)) continue;  // skip the baked/combined static NPC
                src = go;
                break;
            }
            if (src == null) return false;

            var template = Object.Instantiate(src);
            template.name = "PlayerModelBodyTemplate";
            // Match the size the NPC had IN-SCENE. Instantiate copies src's LOCAL scale, but the shopkeeper
            // sits under scaled parents, so its real on-screen size is the WORLD (lossy) scale. A parentless
            // clone with only the local scale renders about twice too big ("12ft"); pin it to the captured
            // world scale.
            template.transform.localScale = src.transform.lossyScale;
            // Deactivate BEFORE stripping: prevents any deferred Shopkeeper.Start (which NREs on a parentless
            // clone) from being scheduled, and guarantees no active duplicate is rendered.
            template.SetActive(false);
            Strip(template);
            Object.DontDestroyOnLoad(template);
            _template = template;
            Plugin.Log.LogInfo("[PlayerModel] Captured a humanoid body template from a shopkeeper");
            return true;
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
