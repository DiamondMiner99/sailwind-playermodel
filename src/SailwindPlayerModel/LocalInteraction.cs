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
        private static readonly AccessTools.FieldRef<GoPointer, GoPointerButton> StickyRef =
            AccessTools.FieldRefAccess<GoPointer, GoPointerButton>("stickyClickedButton");
        private static readonly AccessTools.FieldRef<GoPointer, GoPointerButton> ClickedRef =
            AccessTools.FieldRefAccess<GoPointer, GoPointerButton>("clickedButton");

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
            if (p == null) return null;
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
                var sticky = StickyRef(p);
                op = sticky != null ? sticky : ClickedRef(p);
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
}
