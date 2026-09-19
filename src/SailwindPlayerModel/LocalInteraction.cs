using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// What the local player is doing with their hands, read straight from the game's own pointer. No
    /// networking: the game already records the control you are operating and the item you hold.
    ///
    /// The wheel, winches and pump are sticky clicks by default: one click grabs the control and it stays held
    /// until you click again, and steering or cranking continues after switching to the orbit camera. The
    /// pointer keeps that in its private stickyClickedButton. With the mouse-steering settings on they are
    /// held while the button is down instead, which lands in clickedButton. Both are read.
    /// </summary>
    internal static class LocalInteraction
    {
        private static GoPointer _pointer;
        // Null when a game update renamed the field. Built here, one try each, because a throwing field
        // initializer fails the whole type, and then Pointer, the seat, the body and the pointer postfix throw too.
        private static readonly AccessTools.FieldRef<GoPointer, GoPointerButton> StickyRef;
        private static readonly AccessTools.FieldRef<GoPointer, GoPointerButton> ClickedRef;

        static LocalInteraction()
        {
            try { StickyRef = AccessTools.FieldRefAccess<GoPointer, GoPointerButton>("stickyClickedButton"); }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning("[Items] GoPointer.stickyClickedButton not found, held controls are not read: " + e.GetBaseException().Message);
            }
            try { ClickedRef = AccessTools.FieldRefAccess<GoPointer, GoPointerButton>("clickedButton"); }
            catch (System.Exception e)
            {
                Plugin.Log?.LogWarning("[Items] GoPointer.clickedButton not found, controls held with the button down are not read: " + e.GetBaseException().Message);
            }
        }

        /// <summary>The local mouse pointer. Cached; found again if the scene replaced it.</summary>
        public static GoPointer Pointer
        {
            get
            {
                if (_pointer != null) return _pointer;
                foreach (var p in Object.FindObjectsOfType<GoPointer>())
                {
                    if (p.type != GoPointer.PointerType.crosshairMouse) continue;
                    _pointer = p;
                    break;
                }
                if (_pointer == null) _pointer = Object.FindObjectOfType<GoPointer>();
                return _pointer;
            }
        }

        /// <summary>The control held with a sticky click (the wheel, a winch, the pump), or null.</summary>
        public static GoPointerButton StickyControl(GoPointer p)
        {
            if (p == null || StickyRef == null) return null;
            try { return StickyRef(p); }
            catch { return null; }
        }

        /// <summary>
        /// This frame's interaction. <paramref name="target"/> is the control or item involved;
        /// <paramref name="held"/> is set whenever an item is in hand, even if a control takes priority.
        /// </summary>
        public static InteractionKind Sample(out Transform target, out PickupableItem held)
        {
            target = null;
            held = null;
            var p = Pointer;
            if (p == null) return InteractionKind.None;

            GoPointerButton op = null;
            try
            {
                var sticky = StickyRef != null ? StickyRef(p) : null;
                op = sticky != null ? sticky : (ClickedRef != null ? ClickedRef(p) : null);
            }
            catch { op = null; }

            held = p.GetHeldItem();

            if (op != null && op.isActiveAndEnabled)
            {
                if (op is GPButtonSteeringWheel) { target = op.transform; return InteractionKind.Helm; }
                if (op is GPButtonRopeWinch || op is BilgePump) { target = op.transform; return InteractionKind.Crank; }
                if (op is GPButtonSailPusher) { target = op.transform; return InteractionKind.Push; }
            }

            if (held != null)
            {
                target = held.transform;
                if (held is PickupableBoatMooringRope || held is MooringRopeLengthAdjuster) return InteractionKind.Rope;
                return held.big ? InteractionKind.CarryBig : InteractionKind.Carry;
            }
            return InteractionKind.None;
        }
    }

    /// <summary>
    /// The hold GoPointer last placed the held item in, relative to the pointer it placed it from. GoPointer places
    /// the item in its LateUpdate, which runs after <see cref="LocalBody"/>'s, so the body reads the item a frame late
    /// and puts this hold on the current pointer instead. A postfix on GoPointer.LateUpdate, registered by
    /// GuardedPatches rather than by attribute, so a game update that renames it skips only this patch.
    /// </summary>
    internal static class PointerPlaced
    {
        internal static PickupableItem Item;
        internal static Quaternion Hold;
        internal static int Frame = -10;
        private static bool _logged;

        internal static void Postfix(GoPointer __instance)
        {
            // Runs after the stock LateUpdate on every frame from the title screen on, so a failure is logged once
            // rather than thrown out of it every frame. A failed frame leaves Frame behind, and LocalBody then uses the
            // item's live rotation.
            try
            {
                // The scene also has two touch pointers; only the local mouse pointer is read.
                if (__instance.type != GoPointer.PointerType.crosshairMouse || __instance != LocalInteraction.Pointer) return;
                var held = __instance.GetHeldItem();
                if (held == null) { Item = null; return; }
                Item = held;
                Hold = Quaternion.Inverse(__instance.transform.rotation) * held.transform.rotation;
                Frame = Time.frameCount;
            }
            catch (System.Exception e)
            {
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogWarning("[Items] pointer hold: " + e);
                }
            }
        }
    }
}
