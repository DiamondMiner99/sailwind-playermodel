using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// Small helpers for working with clones of Sailwind's own start-menu scroll. Shared so that a mod
    /// adding a button to <see cref="ModPauseMenu"/> builds it the same way the menu builds its own.
    /// </summary>
    public static class MenuUtil
    {
        /// <summary>
        /// Clone a menu button from a template onto a panel, if one of that name is not already there. The
        /// clone keeps the native StartMenuButton component and layer 5, which is what makes it clickable by
        /// the game's own menu raycast rather than needing one of our own.
        /// </summary>
        public static void EnsureButton(Transform panel, Transform template, string name, Vector3 localPos)
        {
            if (FindChild(panel, name) != null) return;
            var clone = Object.Instantiate(template.gameObject, panel);
            clone.name = name;
            clone.transform.localRotation = template.localRotation;
            clone.transform.localScale = template.localScale;
            clone.transform.localPosition = localPos;
        }

        /// <summary>Set a button's visible text. Sailwind's UI is world-space TextMesh, not TextMeshPro.</summary>
        public static void SetLabel(Transform button, string text)
        {
            if (button == null) return;
            var t = button.Find("text");
            if (t == null) return;
            var tm = t.GetComponent<TextMesh>();
            if (tm != null) tm.text = text;
        }

        /// <summary>Depth-first child search by exact name.</summary>
        public static Transform FindChild(Transform root, string name)
        {
            if (root == null) return null;
            var d = root.Find(name);
            if (d != null) return d;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindChild(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        public static void SetActive(Transform t, bool on)
        {
            if (t != null && t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
        }
    }
}
