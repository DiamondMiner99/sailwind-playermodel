using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>How a body holds one kind of item.</summary>
    internal enum HoldStyle
    {
        OneHand,       // small things: food, candles, tobacco, ink, fuel wood
        Mug,           // cups: gripped round the side, tipped at the mouth
        Bottle,        // bottles: gripped round the body, neck to the mouth
        Hang,          // lanterns and chimes: hung from the top ring
        Handle,        // kettle top handle, bucket rim
        Tool,          // knife, hammer: gripped by the handle, swings with the item
        Pipe,          // bowl in the palm, stem to the mouth
        TwoHandSides,  // pots, small barrels, big food: a hand on each side
        Flat,          // compasses, chronometers: level in both palms at reading height
        Board,         // gauges, wall clocks, planispheres, books held open: standing, face toward the holder
        Scroll,        // a hand on each roller, one above the other, unrolling vertically
        Map,           // a hand on each edge, unrolling horizontally
        Quadrant,      // sighted along the top edge at the eye
        Spyglass,      // eyepiece at the eye, both hands on the tube
        Big,           // crates and barrels: both hands on the near sides, carried against the chest
        Broom,         // both hands on the stick, head on the deck ahead, sweeps with the game's own sweep
        Reach,         // oar, chip log, anchor: left where the game has them, hands reach the grips
        Rod,           // fishing rod: held like a pole to cast, swung from the hands, a hand on the reel with a line out
    }

    /// <summary>Live tuning for carried items. Every value but FoodTwoHandsMinLength applies immediately from the F1 menu.</summary>
    public static class ItemPoseTuning
    {
        private const string Section = "6. Item Poses";

        public static ConfigEntry<float> CarryRight { get; private set; }
        public static ConfigEntry<float> CarryUp { get; private set; }
        public static ConfigEntry<float> CarryForward { get; private set; }
        public static ConfigEntry<float> ReadUp { get; private set; }
        public static ConfigEntry<float> ReadForward { get; private set; }
        public static ConfigEntry<float> ReadTilt { get; private set; }
        public static ConfigEntry<float> MouthForward { get; private set; }
        public static ConfigEntry<float> MouthUp { get; private set; }
        public static ConfigEntry<float> BigCarryGap { get; private set; }
        public static ConfigEntry<float> LanternDrop { get; private set; }
        public static ConfigEntry<float> FoodTwoHandsMinLength { get; private set; }
        public static ConfigEntry<float> MaxWristBend { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            CarryRight = cfg.Bind(Section, "CarryRight", 0.16f, new ConfigDescription(
                "Where a one-handed item is held: meters to the right of the chest.", new AcceptableValueRange<float>(-0.2f, 0.5f)));
            CarryUp = cfg.Bind(Section, "CarryUp", -0.30f, new ConfigDescription(
                "Where a one-handed item is held: meters above the chest (negative is lower).", new AcceptableValueRange<float>(-0.7f, 0.3f)));
            CarryForward = cfg.Bind(Section, "CarryForward", 0.30f, new ConfigDescription(
                "Where a one-handed item is held: meters in front of the chest.", new AcceptableValueRange<float>(0.05f, 0.7f)));
            ReadUp = cfg.Bind(Section, "ReadUp", -0.14f, new ConfigDescription(
                "Height of a scroll, map or compass being read, in meters above the chest.", new AcceptableValueRange<float>(-0.6f, 0.3f)));
            ReadForward = cfg.Bind(Section, "ReadForward", 0.36f, new ConfigDescription(
                "Distance of a scroll, map or compass being read, in meters in front of the chest.", new AcceptableValueRange<float>(0.15f, 0.7f)));
            ReadTilt = cfg.Bind(Section, "ReadTilt", 24f, new ConfigDescription(
                "How far a map, scroll or compass is tipped toward the face, in degrees.", new AcceptableValueRange<float>(-30f, 80f)));
            MouthForward = cfg.Bind(Section, "MouthForward", 0.13f, new ConfigDescription(
                "Where drinks, food and pipes are brought to: meters in front of the head bone, measured along the face.", new AcceptableValueRange<float>(0f, 0.3f)));
            MouthUp = cfg.Bind(Section, "MouthUp", 0f, new ConfigDescription(
                "Where drinks, food and pipes are brought to: meters above the head bone, measured along the face.", new AcceptableValueRange<float>(-0.2f, 0.2f)));
            if (!MouthOffsetsAlreadyMoved(cfg))
            {
                MoveOldDefault(MouthForward, 0.11f);
                MoveOldDefault(MouthUp, 0.02f);
            }
            BigCarryGap = cfg.Bind(Section, "BigCarryGap", 0.10f, new ConfigDescription(
                "Space between the chest and a crate or barrel being carried, in meters.", new AcceptableValueRange<float>(0f, 0.5f)));
            LanternDrop = cfg.Bind(Section, "LanternDrop", -0.30f, new ConfigDescription(
                "Height of the hand holding a lantern, in meters above the chest.", new AcceptableValueRange<float>(-0.7f, 0.2f)));
            FoodTwoHandsMinLength = cfg.Bind(Section, "FoodTwoHandsMinLength", 0.28f, new ConfigDescription(
                "Food at least this long (meters) is held and eaten with both hands: loaves, whole fish, big cheese. Each piece of food keeps the hold it got the first time it was held, so a change applies to food not held yet.", new AcceptableValueRange<float>(0.1f, 1f)));
            MaxWristBend = cfg.Bind(Section, "MaxWristBend", 60f, new ConfigDescription(
                "Most the wrist bends away from the forearm to match a grip, in degrees.", new AcceptableValueRange<float>(10f, 90f)));
        }

        /// <summary>
        /// The mouth offsets used to be measured along the body's heading and world up, with defaults of 0.11 and 0.02,
        /// which put the mouth on the upper lip and inside the face. They are measured along the face now, with new
        /// defaults. BepInEx keeps whatever value a config file already holds, so a value still at its old default is
        /// moved to the new one here. That happens once, on the first launch of this release: the marker
        /// MouthOffsetsAlreadyMoved reads and writes is what says it has been done.
        /// </summary>
        private static void MoveOldDefault(ConfigEntry<float> entry, float oldDefault)
        {
            try
            {
                if (Mathf.Abs(entry.Value - oldDefault) > 1e-4f) return;
                float now = (float)entry.DefaultValue;
                entry.Value = now;
                Plugin.Log.LogInfo($"[ItemPose] {entry.Definition.Key} was at the old default {oldDefault:F2}, set to the new default {now:F2}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[ItemPose] Could not move {entry.Definition.Key} off its old default: {e.Message}");
            }
        }

        /// <summary>
        /// Whether the move above has already been done, recorded in a marker entry the F1 menu does not show.
        /// A config file written before this release has no marker, so it still moves off the old defaults on its
        /// first launch; after that a player who sets 0.11 or 0.02 back on purpose keeps it. The marker is written
        /// before the move rather than after, so a move that throws cannot leave this running on every launch.
        /// False when the marker cannot be bound at all, which leaves the move behaving as it did before.
        /// </summary>
        private static bool MouthOffsetsAlreadyMoved(ConfigFile cfg)
        {
            try
            {
                // BrowsableAttribute is what ConfigurationManager reads to keep an entry out of its list, and
                // BepInEx ignores tags it does not know, so nothing here needs ConfigurationManager installed.
                var marker = cfg.Bind(Section, "MouthOffsetsMoved", false, new ConfigDescription(
                    "Bookkeeping, not a setting: records that the mouth offsets have been moved off their old defaults.",
                    null, new System.ComponentModel.BrowsableAttribute(false)));
                if (marker.Value) return true;
                marker.Value = true;
                return false;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[ItemPose] Could not read the mouth-offset marker: " + e.Message);
                return false;
            }
        }
    }

    internal sealed class HoldProfile
    {
        public HoldStyle Style;
        public Transform Root;
        public PickupableItem Item;
        public ShipItemBottle Bottle;
        public ShipItemFood Food;
        public ShipItemPipe Pipe;
        public ShipItemSpyglass Spyglass;
        public Transform SweepFrame;
        public Transform ReachFrame;
        public Transform RodTip, RodSpinner;
        public float Line;               // a fishing rod's line out, eased 0 to 1
        public bool Kettle, Knife, Rod, ChipLog, Anchor;
        public Bounds Local;
        public Mesh LastMesh;
        public MeshFilter MainFilter;
        public float PickupTime;
        public int LastFrame = -10;
        public int BuiltFrame;
        public float Use, Open, Aim = 1f, MapHalfWidth = -1f;
        public float RestSpin;           // the holder's turn of the item before an eat or drink began
        public Vector3 Handle;           // a cup's handle, item-local; zero when the cup has none
        public Quaternion CarryRel = Quaternion.identity;   // a big item's upright carry, relative to the view's heading
        public bool HasCarryRel;
        // Realistic Skies quintant: sighted through its telescope, and lowered while its reading is inspected.
        public bool Quintant;
        public Transform Scope;
        public System.Reflection.FieldInfo Inspecting;
    }

    internal struct HandGrip
    {
        public bool On;
        public Vector3 Palm, Finger, Normal;
        /// <summary>A bar, handle or rope running through the fist, in world space; zero for a flat or cupped hold.</summary>
        public Vector3 Axis;
    }

    internal struct ItemPoseResult
    {
        public bool Repose;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 ScaleMul;
        public HandGrip R, L;
    }

    internal struct BodyFrame
    {
        public Vector3 Chest, Head, Mouth, Eye, Feet, Right, Forward;
        public Quaternion Yaw;
        public float LookPitch;          // degrees, positive looking up
        public bool HasPointer;          // the local player's own pointer rotation is known
        public Quaternion PointerRot;
    }

    internal static class ItemPoses
    {
        private static readonly Dictionary<int, HoldProfile> _profiles = new Dictionary<int, HoldProfile>();

        private static readonly AccessTools.FieldRef<ShipItemBottle, float> BottleInitial =
            AccessTools.FieldRefAccess<ShipItemBottle, float>("initialHoldDistance");
        private static readonly AccessTools.FieldRef<ShipItemFood, float> FoodInitial =
            AccessTools.FieldRefAccess<ShipItemFood, float>("initialHoldDistance");
        private static readonly AccessTools.FieldRef<ShipItemPipe, float> PipeInitial =
            AccessTools.FieldRefAccess<ShipItemPipe, float>("initialHoldDistance");
        private static readonly AccessTools.FieldRef<ShipItemBroom, Cleaner> BroomCleaner =
            AccessTools.FieldRefAccess<ShipItemBroom, Cleaner>("cleaner");

        public static HoldProfile Profile(Transform item)
        {
            if (item == null) return null;
            int id = item.GetInstanceID();
            HoldProfile p;
            // A profile that measured nothing is built again, in case whatever hid the item's size has passed.
            if (!_profiles.TryGetValue(id, out p) || p.Root == null || (p.Local.size.sqrMagnitude < 1e-6f && Time.frameCount - p.BuiltFrame > 30))
            {
                if (_profiles.Count > 96) Prune();
                p = Build(item);
                _profiles[id] = p;
            }
            // A fresh hold restarts the pickup animations (scroll unroll).
            if (Time.frameCount - p.LastFrame > 2)
            {
                p.PickupTime = Time.time;
                p.Use = 0f;
                p.Open = 0f;
                p.MapHalfWidth = -1f;
                p.HasCarryRel = false;
                p.Aim = 1f;
            }
            p.LastFrame = Time.frameCount;
            // Maps fold and scrolls open by swapping the mesh; measure again when that happens.
            if (p.MainFilter != null && p.MainFilter.sharedMesh != p.LastMesh)
            {
                p.LastMesh = p.MainFilter.sharedMesh;
                p.Local = p.Style == HoldStyle.Big && p.LastMesh != null ? p.LastMesh.bounds : MeasureLocal(item);
            }
            return p;
        }

        /// <summary>Whether this item is drawn by the body rather than left where the game holds it.</summary>
        public static bool Reposes(Transform item)
        {
            var p = Profile(item);
            return p != null && p.Style != HoldStyle.Reach;
        }

        private static void Prune()
        {
            var dead = new List<int>();
            foreach (var kv in _profiles)
                if (kv.Value.Root == null || Time.frameCount - kv.Value.LastFrame > 600) dead.Add(kv.Key);
            foreach (var k in dead) _profiles.Remove(k);
        }

        private static HoldProfile Build(Transform item)
        {
            var p = new HoldProfile { Root = item, Item = item.GetComponent<PickupableItem>(), BuiltFrame = Time.frameCount };
            p.MainFilter = item.GetComponent<MeshFilter>();
            p.LastMesh = p.MainFilter != null ? p.MainFilter.sharedMesh : null;
            p.Local = MeasureLocal(item);
            p.Style = Classify(p);
            // A big item is sized by its own body mesh when it has one. A stove's chimney pipe or a smoke
            // stack otherwise doubles its height and the pose hangs it through the deck.
            if (p.Style == HoldStyle.Big && p.MainFilter != null && p.MainFilter.sharedMesh != null)
                p.Local = p.MainFilter.sharedMesh.bounds;
            if (p.Style == HoldStyle.Mug) p.Handle = CupHandle(p.Local);
            Plugin.Log.LogInfo($"[ItemPose] '{item.name}' held as {p.Style} (size {p.Local.size.x:F2} x {p.Local.size.y:F2} x {p.Local.size.z:F2})" +
                (p.Style == HoldStyle.Mug ? (p.Handle != Vector3.zero ? $", handle at {p.Handle.x:F3}, {p.Handle.y:F3}, {p.Handle.z:F3}" : ", no handle") : ""));
            return p;
        }

        private static HoldStyle Classify(HoldProfile p)
        {
            var it = p.Item;
            float longest = Mathf.Max(p.Local.size.x, Mathf.Max(p.Local.size.y, p.Local.size.z));
            if (it == null) return longest >= 0.36f ? HoldStyle.TwoHandSides : HoldStyle.OneHand;

            if (it is ShipItemBroom)
            {
                var cleaner = BroomCleaner((ShipItemBroom)it);
                p.SweepFrame = cleaner != null ? cleaner.transform.parent : null;
                return HoldStyle.Broom;
            }
            // Items from other mods, matched by class name so this mod needs none of them installed. These come
            // first: two of them are built on the game's hangable class and would otherwise hang like lanterns.
            switch (it.GetType().Name)
            {
                case "ModItemQuintant":   // Realistic Skies: a quadrant with a telescope, held the same way
                    p.Quintant = true;
                    p.Scope = p.Root.Find("index/scope");
                    p.Inspecting = AccessTools.Field(it.GetType(), "inspecting");
                    return HoldStyle.Quadrant;
                case "ModItemPlanisphere":  // Realistic Skies
                case "ModItemAlmanac":      // Realistic Skies
                case "ModItemCharts":       // Realistic Skies celestial atlas
                case "ShipItemBarometer":   // Climate
                case "ShipItemHygrometer":  // Climate
                case "ShipItemThermometer": // Climate
                    return HoldStyle.Board;
            }

            if (it is ShipItemOar) { p.ReachFrame = ((ShipItemOar)it).waterPos; return HoldStyle.Reach; }
            if (it is ShipItemFishingRod)
            {
                p.Rod = true;
                // The rod that shows is a bending skinned rod under 'rod rotator/fishing'; the item's own rod mesh is
                // hidden. It is drawn in the hands from that frame, with the line's end at 'fishing_line_att' and the
                // reel handle on 'fishing_rod_spinner'. A rod built some other way is left where the game holds it.
                p.ReachFrame = p.Root.Find("rod rotator/fishing");
                p.RodTip = p.Root.Find("rod rotator/fishing/fishing_line_att");
                p.RodSpinner = p.Root.Find("rod rotator/fishing/fishing_rod_spinner");
                return p.ReachFrame != null ? HoldStyle.Rod : HoldStyle.Reach;
            }
            if (it is ShipItemChipLog) { p.ChipLog = true; return HoldStyle.Reach; }
            if (it is Anchor) { p.Anchor = true; return HoldStyle.Reach; }
            if (it is ShipItemLight || it is ShipItemHangable) return HoldStyle.Hang;
            if (it is ShipItemBottle)
            {
                p.Bottle = (ShipItemBottle)it;
                float cap = p.Bottle.GetCapacity();
                if (cap >= 30f) return HoldStyle.Big;
                if (cap == 9f) return HoldStyle.Handle;
                return cap < 5f ? HoldStyle.Mug : HoldStyle.Bottle;
            }
            if (it is ShipItemFood)
            {
                p.Food = (ShipItemFood)it;
                return longest >= ItemPoseTuning.FoodTwoHandsMinLength.Value ? HoldStyle.TwoHandSides : HoldStyle.OneHand;
            }
            if (it is ShipItemPipe) { p.Pipe = (ShipItemPipe)it; return HoldStyle.Pipe; }
            if (it is ShipItemKnife) { p.Knife = true; return HoldStyle.Tool; }
            if (it is ShipItemHammer) return HoldStyle.Tool;
            if (it is ShipItemScroll) return HoldStyle.Scroll;
            if (it is ShipItemFoldable) return HoldStyle.Map;
            // A compass or chronometer lies flat (thin on Y); a wall clock stands (thin on Z) with its face toward
            // the holder, and is read like a board.
            if (it is ShipItemCompass || it is ShipItemClock) return p.Local.size.z < p.Local.size.y * 0.5f ? HoldStyle.Board : HoldStyle.Flat;
            if (it is ShipItemQuadrant) return HoldStyle.Quadrant;
            if (it is ShipItemSpyglass) { p.Spyglass = (ShipItemSpyglass)it; return HoldStyle.Spyglass; }
            if (it is ShipItemKettle) { p.Kettle = true; return HoldStyle.Handle; }
            if (it is ShipItemSoup) return HoldStyle.TwoHandSides;
            // A crate the size of a tea box is held like one; only a real armful is hugged to the chest.
            if (it is ShipItemCrate || it.big) return longest >= 0.6f ? HoldStyle.Big : HoldStyle.TwoHandSides;
            return longest >= 0.36f ? HoldStyle.TwoHandSides : HoldStyle.OneHand;
        }

        /// <summary>
        /// Bounds of the item's visible meshes in its own local space. Only enabled renderers count: some
        /// instruments carry large helper quads with their renderers switched off.
        /// </summary>
        private static Bounds MeasureLocal(Transform root)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || !mr.enabled) continue;
                Encapsulate(root, mf.transform, mf.sharedMesh.bounds, ref b, ref any);
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (smr.sharedMesh == null || !smr.enabled) continue;
                Encapsulate(root, smr.transform, smr.sharedMesh.bounds, ref b, ref any);
            }
            return any ? b : new Bounds(Vector3.zero, Vector3.one * 0.15f);
        }

        /// <summary>
        /// Adds a mesh's bounds to <paramref name="b"/>, in the root's local space. The child's frame is built from
        /// the local transforms between it and the root, never through world space: an item in a crate or the
        /// inventory is shrunk to zero scale, and on the frame it comes back out a trip through world space
        /// collapses every corner onto one point. The spyglass measured 0 x 0 x 0 that way, which put its middle
        /// at the eye and the rest of the tube through the head.
        /// </summary>
        private static void Encapsulate(Transform root, Transform t, Bounds mb, ref Bounds b, ref bool any)
        {
            Matrix4x4 toRoot = Matrix4x4.identity;
            for (Transform x = t; x != null && x != root; x = x.parent)
                toRoot = Matrix4x4.TRS(x.localPosition, x.localRotation, x.localScale) * toRoot;
            Vector3 c = mb.center, e = mb.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x), c.y + ((i & 2) == 0 ? -e.y : e.y), c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 local = toRoot.MultiplyPoint3x4(corner);
                if (!any) { b = new Bounds(local, Vector3.zero); any = true; }
                else b.Encapsulate(local);
            }
        }

        // ---- per frame --------------------------------------------------------------------------------------

        public static void Solve(HoldProfile p, Vector3 floatPos, Quaternion floatRot, BodyFrame f, float dt, out ItemPoseResult r)
        {
            r = new ItemPoseResult { ScaleMul = Vector3.one, Rot = floatRot, Pos = floatPos };
            Transform item = p.Root;
            Bounds b = p.Local;
            Vector3 c = b.center, e = b.extents;
            Vector3 s = item.lossyScale;
            Vector3 R = f.Right, U = Vector3.up, F = f.Forward;

            // What the game is doing with the item, relative to the holder's view: a swing, a tilt to drink, a
            // throw being charged. Remote bodies have no pointer, so their view is estimated from the head.
            Quaternion view = f.HasPointer ? f.PointerRot : f.Yaw * Quaternion.Euler(-f.LookPitch, 0f, 0f);
            Quaternion rel = Quaternion.Inverse(view) * floatRot;

            float useTarget = UseProgress(p, floatPos, f);
            p.Use = Mathf.Lerp(p.Use, useTarget, 1f - Mathf.Exp(-14f * dt));
            float use = p.Use;

            // The holder's own turn of the item: the scroll wheel adds to heldRotationOffset and the game draws the
            // item turned by it about the view's right axis. An item the game also tips to eat or drink keeps the
            // turn it had before the use began, and the use pose takes over from there.
            float spin = PlayerSpin(p, rel, f);
            // Food is held the same way up whichever way over it has been turned. ShipItemFood.OnAltHeld spins food
            // toward -180 degrees to bring it to the mouth and nothing turns it back, so after the first bite the
            // game holds it upside down, and read as a turn that put the hands under a loaf or a cheese on top of it.
            if (p.Food != null)
            {
                if (spin > 90f) spin -= 180f;
                else if (spin < -90f) spin += 180f;
            }
            if (use < 0.02f) p.RestSpin = spin;
            float restSpin = p.Bottle != null || p.Food != null || p.Pipe != null ? p.RestSpin : spin;

            Vector3 anchorOne = f.Chest + R * ItemPoseTuning.CarryRight.Value + U * ItemPoseTuning.CarryUp.Value + F * ItemPoseTuning.CarryForward.Value;
            Vector3 readCenter = f.Chest + U * ItemPoseTuning.ReadUp.Value + F * ItemPoseTuning.ReadForward.Value;
            // The reading tilt gives way to the holder's own turn: a chart turned flat in first person is flat here too.
            float readTilt = ItemPoseTuning.ReadTilt.Value * Mathf.Clamp01(1f - Mathf.Abs(spin) / 90f);
            Quaternion readRot = f.Yaw * Quaternion.Euler(-readTilt, 0f, 0f);

            switch (p.Style)
            {
                case HoldStyle.OneHand:
                {
                    Vector3 grip = new Vector3(b.max.x, c.y, c.z);
                    Quaternion rot = f.Yaw * Quaternion.Euler(-8f, 0f, 0f);
                    Vector3 pos = anchorOne - rot * Vector3.Scale(grip, s);
                    Spin(ref pos, ref rot, anchorOne, R, restSpin);
                    if (p.Food != null && use > 0.001f)
                    {
                        Quaternion ru = f.Yaw * Quaternion.Euler(-25f, 0f, 0f);
                        Vector3 bite = new Vector3(c.x, b.max.y, b.min.z);
                        Vector3 pu = f.Mouth - ru * Vector3.Scale(bite, s);
                        pos = Vector3.Lerp(pos, pu, use); rot = Quaternion.Slerp(rot, ru, use);
                    }
                    Place(ref r, pos, rot);
                    r.R = Grip(pos, rot, s, grip, new Vector3(-1f, 0f, 0f), new Vector3(0f, -0.3f, 1f));
                    break;
                }

                case HoldStyle.Mug:
                case HoldStyle.Bottle:
                {
                    bool mug = p.Style == HoldStyle.Mug;
                    bool byHandle = mug && p.Handle != Vector3.zero;
                    float rad = Mathf.Min(e.x, e.z);
                    Quaternion turn = Quaternion.identity;
                    Vector3 grip, mouthLocal;
                    if (byHandle)
                    {
                        // The cup is turned so its handle points out at the right hand and a little back toward the
                        // holder: the fingers go through the handle and the cup sits in front of the fist. Drinking
                        // brings the rim on the holder's side to the mouth.
                        Vector3 handleDir = new Vector3(p.Handle.x, 0f, p.Handle.z);
                        turn = Quaternion.Euler(0f, Vector3.SignedAngle(handleDir, new Vector3(0.9f, 0f, -0.42f), Vector3.up), 0f);
                        grip = p.Handle;
                        Vector3 toHolder = Quaternion.Inverse(turn) * Vector3.back;
                        mouthLocal = new Vector3(toHolder.x * rad * 0.9f, b.max.y, toHolder.z * rad * 0.9f);
                    }
                    else if (mug)
                    {
                        grip = new Vector3(c.x + rad * 0.78f, b.min.y + b.size.y * 0.45f, c.z - rad * 0.5f);
                        mouthLocal = new Vector3(c.x, b.max.y, b.min.z * 0.9f + c.z * 0.1f);
                    }
                    else
                    {
                        grip = new Vector3(c.x + e.x * 0.95f, c.y - e.y * 0.1f, c.z);
                        mouthLocal = new Vector3(c.x, b.max.y, c.z);
                    }
                    Quaternion rot = f.Yaw * turn;
                    Vector3 pos = anchorOne - rot * Vector3.Scale(grip, s);
                    Spin(ref pos, ref rot, anchorOne, R, restSpin);
                    if (use > 0.001f)
                    {
                        float tilt = p.Bottle != null ? (f.HasPointer ? Mathf.Clamp(p.Bottle.heldRotationOffset, -150f, 60f) : -p.Bottle.maxRot * use) : -100f * use;
                        Quaternion ru = f.Yaw * Quaternion.Euler(Mathf.Min(tilt, 0f), 0f, 0f) * turn;
                        Vector3 pu = f.Mouth - ru * Vector3.Scale(mouthLocal, s);
                        pos = Vector3.Lerp(pos, pu, use); rot = Quaternion.Slerp(rot, ru, use);
                    }
                    Place(ref r, pos, rot);
                    r.R = byHandle ? Bar(pos, rot, s, grip, Vector3.up)
                        : mug ? Grip(pos, rot, s, grip, new Vector3(-0.8f, 0f, 0.5f), new Vector3(-0.35f, 0f, 1f))
                              : Grip(pos, rot, s, grip, new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f));
                    break;
                }

                case HoldStyle.Hang:
                {
                    Vector3 ring = new Vector3(c.x, b.max.y - Mathf.Min(0.015f, e.y * 0.2f), c.z);
                    Vector3 hand = f.Chest + R * (ItemPoseTuning.CarryRight.Value + 0.06f) + U * ItemPoseTuning.LanternDrop.Value + F * (ItemPoseTuning.CarryForward.Value - 0.06f);
                    Quaternion rot = f.Yaw;
                    Vector3 pos = hand - rot * Vector3.Scale(ring, s);
                    Place(ref r, pos, rot);
                    r.R = Grip(pos, rot, s, ring, new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, 1f));
                    break;
                }

                case HoldStyle.Handle:
                {
                    Vector3 grip = p.Kettle ? new Vector3(c.x, b.max.y - 0.015f, c.z)
                                            : new Vector3(c.x + e.x * 0.9f, b.max.y - 0.02f, c.z);
                    Vector3 hand = f.Chest + R * (ItemPoseTuning.CarryRight.Value + 0.06f) + U * (ItemPoseTuning.LanternDrop.Value - 0.04f) + F * (ItemPoseTuning.CarryForward.Value - 0.08f);
                    Quaternion rot = f.Yaw;
                    Vector3 pos = hand - rot * Vector3.Scale(grip, s);
                    // A kettle hangs from its top handle; a bucket held by the rim turns with the holder.
                    if (!p.Kettle) Spin(ref pos, ref rot, hand, R, restSpin);
                    if (p.Bottle != null && use > 0.001f)
                    {
                        Quaternion ru = f.Yaw * Quaternion.Euler(-70f * use, 0f, 0f);
                        Vector3 rim = new Vector3(c.x, b.max.y, b.min.z);
                        Vector3 pu = f.Mouth - ru * Vector3.Scale(rim, s);
                        pos = Vector3.Lerp(pos, pu, use); rot = Quaternion.Slerp(rot, ru, use);
                    }
                    Place(ref r, pos, rot);
                    r.R = p.Kettle ? Grip(pos, rot, s, grip, new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, 1f))
                                   : Grip(pos, rot, s, grip, new Vector3(-1f, 0f, 0f), new Vector3(0f, -0.2f, 1f));
                    if (p.Bottle != null && use > 0.3f)
                        r.L = Grip(pos, rot, s, new Vector3(c.x - e.x * 0.9f, c.y, c.z), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f));
                    break;
                }

                case HoldStyle.Tool:
                {
                    Vector3 grip = p.Knife ? new Vector3(c.x, b.max.y - 0.05f, b.min.z + 0.12f)
                                           : new Vector3(c.x, c.y - 0.02f, b.min.z + 0.13f);
                    Vector3 hand = f.Chest + R * (ItemPoseTuning.CarryRight.Value + 0.04f) + U * ItemPoseTuning.CarryUp.Value + F * (ItemPoseTuning.CarryForward.Value - 0.04f);
                    // The game's swing (heldRotationOffset) arrives in `rel`; pivot it on the hand.
                    Quaternion rot = f.Yaw * Quaternion.Euler(-8f, 0f, 0f) * rel;
                    Vector3 pos = hand - rot * Vector3.Scale(grip, s);
                    Place(ref r, pos, rot);
                    r.R = Grip(pos, rot, s, grip, new Vector3(-1f, 0f, 0f), new Vector3(0f, -1f, 0f));
                    break;
                }

                case HoldStyle.Pipe:
                {
                    Vector3 bowl = new Vector3(c.x, c.y - 0.01f, c.z + e.z * 0.7f);
                    Vector3 mouthpiece = new Vector3(c.x, b.max.y - 0.01f, b.min.z);
                    Quaternion rot = f.Yaw;
                    Vector3 hand = f.Chest + R * (ItemPoseTuning.CarryRight.Value - 0.02f) + U * (ItemPoseTuning.CarryUp.Value + 0.04f) + F * (ItemPoseTuning.CarryForward.Value - 0.06f);
                    Vector3 pos = hand - rot * Vector3.Scale(bowl, s);
                    Spin(ref pos, ref rot, hand, R, restSpin);
                    if (use > 0.001f)
                    {
                        // The stem dips from the lips, pivoting on the mouthpiece, so the bowl hangs below the mouth. The
                        // game's pipe never tips (its maxRot is 0), so the angle is the pose's own. The palm stays on the
                        // bowl; the wrist limit keeps the hand from following the whole dip.
                        Quaternion ru = f.Yaw * Quaternion.Euler(14f * use, 0f, 0f);
                        Vector3 pu = f.Mouth - ru * Vector3.Scale(mouthpiece, s);
                        pos = Vector3.Lerp(pos, pu, use); rot = Quaternion.Slerp(rot, ru, use);
                    }
                    Place(ref r, pos, rot);
                    r.R = Grip(pos, rot, s, bowl, new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f));
                    break;
                }

                case HoldStyle.TwoHandSides:
                {
                    bool alongX = e.x >= e.z;
                    bool food = p.Food != null;
                    Quaternion rot = f.Yaw * Quaternion.Euler(-8f, 0f, 0f);
                    float depth = alongX ? e.z : e.x;
                    Vector3 center = f.Chest + U * (ItemPoseTuning.CarryUp.Value + 0.06f) + F * (ItemPoseTuning.CarryForward.Value + depth * 0.5f);
                    Vector3 pos = center - rot * Vector3.Scale(c, s);
                    Spin(ref pos, ref rot, center, R, restSpin);
                    if (food && use > 0.001f)
                    {
                        Quaternion ru = f.Yaw * Quaternion.Euler(-18f, 0f, 0f);
                        Vector3 bite = alongX ? new Vector3(c.x + e.x * 0.55f, b.max.y, b.min.z) : new Vector3(c.x, b.max.y, b.min.z);
                        Vector3 pu = f.Mouth - ru * Vector3.Scale(bite, s);
                        pos = Vector3.Lerp(pos, pu, use); rot = Quaternion.Slerp(rot, ru, use);
                    }
                    Place(ref r, pos, rot);
                    if (food)
                    {
                        Vector3 gr = alongX ? new Vector3(c.x + e.x * 0.55f, b.min.y, c.z) : new Vector3(c.x + e.x * 0.6f, b.min.y, c.z - e.z * 0.3f);
                        Vector3 gl = alongX ? new Vector3(c.x - e.x * 0.55f, b.min.y, c.z) : new Vector3(c.x - e.x * 0.6f, b.min.y, c.z - e.z * 0.3f);
                        r.R = Grip(pos, rot, s, gr, new Vector3(0f, 1f, 0f), new Vector3(-0.3f, 0f, 1f));
                        r.L = Grip(pos, rot, s, gl, new Vector3(0f, 1f, 0f), new Vector3(0.3f, 0f, 1f));
                    }
                    else
                    {
                        r.R = Grip(pos, rot, s, new Vector3(b.max.x, c.y + e.y * 0.2f, c.z), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0.2f, 1f));
                        r.L = Grip(pos, rot, s, new Vector3(b.min.x, c.y + e.y * 0.2f, c.z), new Vector3(1f, 0f, 0f), new Vector3(0f, 0.2f, 1f));
                    }
                    break;
                }

                case HoldStyle.Flat:
                {
                    Quaternion rot = readRot;
                    Vector3 pos = readCenter - rot * Vector3.Scale(c, s);
                    Spin(ref pos, ref rot, readCenter, R, spin);
                    Place(ref r, pos, rot);
                    r.R = Grip(pos, rot, s, new Vector3(c.x + e.x * 0.9f, b.min.y, c.z - e.z * 0.15f), new Vector3(0f, 1f, 0f), new Vector3(-0.55f, 0f, 1f));
                    r.L = Grip(pos, rot, s, new Vector3(c.x - e.x * 0.9f, b.min.y, c.z - e.z * 0.15f), new Vector3(0f, 1f, 0f), new Vector3(0.55f, 0f, 1f));
                    break;
                }

                case HoldStyle.Board:
                {
                    // Read standing up, face toward the holder (local -Z, like the game's charts and wall clock), so
                    // tipping it toward the eyes takes the top edge away.
                    Quaternion rot = f.Yaw * Quaternion.Euler(readTilt, 0f, 0f);
                    Vector3 center = readCenter + U * 0.04f;
                    Vector3 pos = center - rot * Vector3.Scale(c, s);
                    Spin(ref pos, ref rot, center, R, spin);
                    FitBelowChin(ref pos, rot, b, s, f);
                    Place(ref r, pos, rot);
                    if (e.x * s.x > 0.4f)
                    {
                        // Wider than the shoulders, like a book held open: both palms under the bottom edge, nearer
                        // the middle than the corners.
                        float gx = Mathf.Min(e.x * 0.8f, 0.3f / Mathf.Max(s.x, 1e-3f));
                        r.R = Grip(pos, rot, s, new Vector3(c.x + gx, b.min.y, c.z), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f));
                        r.L = Grip(pos, rot, s, new Vector3(c.x - gx, b.min.y, c.z), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f));
                    }
                    else
                    {
                        r.R = Grip(pos, rot, s, new Vector3(b.max.x, c.y - e.y * 0.2f, c.z), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0.3f, 1f));
                        r.L = Grip(pos, rot, s, new Vector3(b.min.x, c.y - e.y * 0.2f, c.z), new Vector3(1f, 0f, 0f), new Vector3(0f, 0.3f, 1f));
                    }
                    break;
                }

                case HoldStyle.Scroll:
                {
                    float t = Mathf.Clamp01((Time.time - p.PickupTime - 0.03f) / 0.42f);
                    p.Open = t * t * (3f - 2f * t);
                    float sy = Mathf.Lerp(0.14f, 1f, p.Open);
                    r.ScaleMul = new Vector3(1f, sy, 1f);
                    // The page faces the holder (local -Z), so tipping its face up toward the eyes takes the top
                    // edge away from them.
                    Quaternion rot = f.Yaw * Quaternion.Euler(readTilt, 0f, 0f);
                    Vector3 sc = Vector3.Scale(s, r.ScaleMul);
                    Vector3 center = readCenter + U * 0.08f;
                    Vector3 pos = center - rot * Vector3.Scale(c, sc);
                    Spin(ref pos, ref rot, center, R, spin);
                    FitBelowChin(ref pos, rot, b, sc, f);
                    Place(ref r, pos, rot);
                    float roller = Mathf.Max(0.02f, e.y - 0.035f);
                    r.R = Bar(pos, rot, sc, new Vector3(c.x + 0.07f, c.y + roller, c.z), Vector3.right);
                    r.L = Bar(pos, rot, sc, new Vector3(c.x - 0.07f, c.y - roller, c.z), Vector3.right);
                    break;
                }

                case HoldStyle.Map:
                {
                    float target = Mathf.Max(0.04f, e.x);
                    if (p.MapHalfWidth < 0f) p.MapHalfWidth = target * 0.15f;
                    p.MapHalfWidth = Mathf.MoveTowards(p.MapHalfWidth, target, dt * 1.6f);
                    // Unrolling: the open mesh is already wide, so squeeze it and let it spread with the hands.
                    float sx = p.MapHalfWidth < target ? Mathf.Clamp(p.MapHalfWidth / target, 0.12f, 1f) : 1f;
                    r.ScaleMul = new Vector3(sx, 1f, 1f);
                    // The chart faces the holder (local -Z), so tipping it up toward the eyes takes the top edge
                    // away from them.
                    Quaternion rot = f.Yaw * Quaternion.Euler(readTilt, 0f, 0f);
                    Vector3 sc = Vector3.Scale(s, r.ScaleMul);
                    Vector3 center = readCenter + U * 0.06f + F * 0.06f;
                    Vector3 pos = center - rot * Vector3.Scale(c, sc);
                    Spin(ref pos, ref rot, center, R, spin);
                    FitBelowChin(ref pos, rot, b, sc, f);
                    Place(ref r, pos, rot);
                    float lx = (p.MapHalfWidth * 0.93f) / sx;
                    r.R = Grip(pos, rot, sc, new Vector3(c.x + lx, c.y - e.y * 0.12f, c.z), new Vector3(0f, 0f, 1f), new Vector3(-1f, 0.1f, 0f));
                    r.L = Grip(pos, rot, sc, new Vector3(c.x - lx, c.y - e.y * 0.12f, c.z), new Vector3(0f, 0f, 1f), new Vector3(1f, 0.1f, 0f));
                    break;
                }

                case HoldStyle.Quadrant when p.Quintant && p.Scope != null:
                {
                    // Sighted through its telescope: the eyepiece (the scope's near end, which slides back toward
                    // the eye on pickup) goes to the right eye with the scope along the look. While the reading is
                    // inspected the instrument turns its dial toward the eye and moves 15 cm closer, which at the
                    // eye would put it through the face, so it comes down to reading height instead.
                    bool inspecting = false;
                    try { inspecting = p.Inspecting != null && (bool)p.Inspecting.GetValue(p.Item); } catch { }
                    p.Aim = Mathf.Lerp(p.Aim, inspecting ? 0f : 1f, 1f - Mathf.Exp(-8f * dt));

                    var scopeMesh = p.Scope.GetComponent<MeshFilter>();
                    Bounds sb = scopeMesh != null && scopeMesh.sharedMesh != null ? scopeMesh.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.1f);
                    Vector3 eyepiece = item.InverseTransformPoint(p.Scope.TransformPoint(new Vector3(sb.center.x, sb.center.y, sb.min.z)));

                    Quaternion aimRot = f.Yaw * Quaternion.Euler(Mathf.Clamp(-f.LookPitch, -45f, 45f), 0f, 0f);
                    Vector3 aimPos = (f.Eye + R * 0.03f + F * 0.02f) - aimRot * Vector3.Scale(eyepiece, s);
                    Quaternion lowRot = f.Yaw;
                    Vector3 lowPos = readCenter + U * 0.06f - lowRot * Vector3.Scale(c, s);
                    Vector3 pos = Vector3.Lerp(lowPos, aimPos, p.Aim);
                    Quaternion rot = Quaternion.Slerp(lowRot, aimRot, p.Aim);
                    Place(ref r, pos, rot);
                    // The frame plate sits to the right of the telescope: the right hand closes on the handle behind
                    // it, and the left hand holds the arc at the bottom.
                    r.R = Bar(pos, rot, s, new Vector3(b.max.x + 0.01f, c.y + e.y * 0.1f, c.z), Vector3.up);
                    r.L = Grip(pos, rot, s, new Vector3(c.x, b.min.y + 0.02f, c.z + e.z * 0.2f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f));
                    break;
                }

                case HoldStyle.Quadrant:
                {
                    Quaternion rot = f.Yaw * Quaternion.Euler(Mathf.Clamp(-f.LookPitch, -45f, 45f), 0f, 0f);
                    Vector3 nearEdge = new Vector3(c.x, b.max.y - 0.02f, b.min.z + 0.06f);
                    Vector3 eye = f.Eye + R * 0.03f + U * -0.05f + F * 0.12f;
                    Vector3 pos = eye - rot * Vector3.Scale(nearEdge, s);
                    Place(ref r, pos, rot);
                    r.R = Grip(pos, rot, s, new Vector3(c.x, b.max.y - 0.05f, b.min.z + 0.14f), new Vector3(-1f, 0f, 0f), new Vector3(0f, -1f, 0.2f));
                    r.L = Grip(pos, rot, s, new Vector3(c.x, b.max.y - 0.05f, Mathf.Min(b.min.z + 0.5f, b.max.z - 0.06f)), new Vector3(1f, 0f, 0f), new Vector3(0f, -1f, 0.2f));
                    break;
                }

                case HoldStyle.Spyglass:
                {
                    float aimTarget = p.Spyglass == null || !f.HasPointer || Mathf.Abs(p.Spyglass.heldRotationOffset) < 1f ? 1f : 0f;
                    p.Aim = Mathf.Lerp(p.Aim, aimTarget, 1f - Mathf.Exp(-8f * dt));
                    Vector3 eyepiece = new Vector3(c.x, c.y, b.min.z);
                    Quaternion aimRot = view;
                    Vector3 aimPos = (f.Eye + R * 0.035f + F * 0.05f) - aimRot * Vector3.Scale(eyepiece, s);
                    Quaternion lowRot = f.Yaw * Quaternion.Euler(40f, 0f, 0f);
                    Vector3 lowPos = (f.Chest + R * 0.1f + U * -0.26f + F * 0.3f) - lowRot * Vector3.Scale(c, s);
                    Vector3 pos = Vector3.Lerp(lowPos, aimPos, p.Aim);
                    Quaternion rot = Quaternion.Slerp(lowRot, aimRot, p.Aim);
                    Place(ref r, pos, rot);
                    float rad = Mathf.Max(0.02f, Mathf.Min(e.x, e.y) * 0.8f);
                    r.R = Bar(pos, rot, s, new Vector3(c.x, c.y - rad * 0.4f, b.min.z + 0.14f), Vector3.forward);
                    r.L = Bar(pos, rot, s, new Vector3(c.x, c.y - rad * 0.4f, Mathf.Min(b.min.z + 0.5f, b.max.z - 0.05f)), Vector3.forward);
                    break;
                }

                case HoldStyle.Big:
                {
                    // Up is whichever of the item's axes the game's hold already has nearest to world up: a table
                    // modeled on its side has its top along local Z, not Y. Stand it on that axis and keep the
                    // heading the game gives it relative to the view.
                    // The holder's scroll-wheel turn is taken out before looking for the up axis, so the item does
                    // not jump from face to face while it is being turned, then put back about the body's right.
                    Quaternion held = f.HasPointer ? Quaternion.AngleAxis(-spin, view * Vector3.right) * floatRot : floatRot;
                    Vector3 upLocal = UpAxis(held);
                    Quaternion upright = Quaternion.FromToRotation(held * upLocal, Vector3.up) * held;
                    Quaternion viewYaw = Quaternion.Euler(0f, view.eulerAngles.y, 0f);
                    // A barrel stops being big while it is drunk from: the game's ShipItemBottle.OnAltHeld sets
                    // big = false and holds it squared to the view like a cup, standing it upright and ignoring the
                    // rotate key, until BarrelCarryPatches carries it again. Keep the carry it had while it was big.
                    // A crewmate's big flag is not synced, so theirs is read from the hold itself: squared to the
                    // view means the item's right axis lies along the view's right.
                    bool squaredToView = p.Bottle != null && (f.HasPointer
                        ? !p.Item.big
                        : Vector3.Dot(rel * Vector3.right, Vector3.right) > 0.995f);
                    if (!squaredToView || !p.HasCarryRel)
                    {
                        p.CarryRel = Quaternion.Inverse(viewYaw) * upright;
                        p.HasCarryRel = true;
                    }
                    Quaternion rot = Quaternion.AngleAxis(restSpin, R) * f.Yaw * p.CarryRel;
                    float hx = Project(rot, e, R), hy = Project(rot, e, U), hz = Project(rot, e, F);
                    // The chest point is the shoulder line, inside the body; the gap is measured from the ribs.
                    Vector3 center = f.Chest + F * (hz + ChestFront + ItemPoseTuning.BigCarryGap.Value) + U * (-hy * 0.92f + 0.02f);
                    if (p.Bottle != null && use > 0.001f)
                    {
                        Quaternion ru = Quaternion.AngleAxis(-35f * use, R) * rot;
                        Vector3 cu = f.Mouth + F * (hz * 0.9f + 0.04f) + U * (-hy * 0.75f);
                        center = Vector3.Lerp(center, cu, use); rot = Quaternion.Slerp(rot, ru, use);
                    }
                    Vector3 pos = center - rot * Vector3.Scale(c, s);
                    Place(ref r, pos, rot);
                    float side = Mathf.Min(hx * 0.82f, 0.24f);
                    Vector3 near = center - F * (hz * 0.8f) + U * (-hy * 0.22f);
                    r.R = new HandGrip { On = true, Palm = near + R * side, Normal = (-R + F * 0.6f).normalized, Finger = (F + U * 0.35f).normalized };
                    r.L = new HandGrip { On = true, Palm = near - R * side, Normal = (R + F * 0.6f).normalized, Finger = (F + U * 0.35f).normalized };
                    break;
                }

                case HoldStyle.Broom:
                {
                    Vector3 head = f.Feet + F * 0.75f + R * 0.1f;
                    Vector3 top = f.Chest + R * 0.16f + F * 0.24f + U * -0.02f;
                    Vector3 axis = (top - head).normalized;
                    Vector3 fwd = Vector3.ProjectOnPlane(F, axis);
                    if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(U, axis);
                    Quaternion rot = Quaternion.LookRotation(fwd.normalized, axis);
                    Vector3 bristles = new Vector3(c.x, b.min.y + 0.1f, c.z);
                    Vector3 pos = head - rot * Vector3.Scale(bristles, s);
                    Place(ref r, pos, rot);
                    // Grips live on the swinging part, so the hands follow the game's own sweep.
                    // The sweeping part rests at identity under the broom (Cleaner lerps it back to zero), so a
                    // point on the stick at rest, pushed through its current offset, is where the stick is now.
                    Transform sweep = p.SweepFrame != null ? p.SweepFrame : item;
                    Vector3 lower = item.InverseTransformPoint(sweep.TransformPoint(new Vector3(c.x, b.max.y - 0.55f, c.z)));
                    Vector3 upper = item.InverseTransformPoint(sweep.TransformPoint(new Vector3(c.x, b.max.y - 0.12f, c.z)));
                    Vector3 stick = item.InverseTransformDirection(sweep.TransformDirection(Vector3.up));
                    r.R = Bar(pos, rot, s, lower, stick);
                    r.L = Bar(pos, rot, s, upper, stick);
                    break;
                }

                case HoldStyle.Rod:
                {
                    // The shown rod runs along 'fishing' local Z (butt -1.35, tip +1.38) about 0.25 up its local Y, with the
                    // reel hanging below it at -0.77 and its handle out to the left. The right hand closes on the rod just
                    // ahead of the reel; the left holds the butt to cast, or turns the reel handle with a line out.
                    Transform fishing = p.ReachFrame;
                    var rod = p.Item as ShipItemFishingRod;
                    float charge = Mathf.Clamp01(-spin / 105f);
                    p.Line = Mathf.Lerp(p.Line, rod != null && RodLineOut(rod, p.RodTip) ? 1f - charge : 0f, 1f - Mathf.Exp(-6f * dt));
                    float line = p.Line;

                    // Out in front at the hip, tip up. Winding up raises the hands toward the shoulder; with a line out
                    // the rod comes down a little and further out, ready to reel.
                    Vector3 grip = f.Chest + R * 0.2f + F * 0.28f + U * -0.38f;
                    grip = Vector3.Lerp(grip, f.Chest + R * 0.22f + F * 0.12f + U * 0.04f, charge);
                    grip = Vector3.Lerp(grip, f.Chest + R * 0.18f + F * 0.36f + U * -0.3f, line);
                    float lift = Mathf.Lerp(40f, 25f, line) * Mathf.Deg2Rad;
                    Vector3 dir = (F * Mathf.Cos(lift) + U * Mathf.Sin(lift) + R * 0.04f).normalized;

                    Quaternion fishingInItem = Quaternion.Inverse(item.rotation) * fishing.rotation;
                    Vector3 gripInItem = item.InverseTransformPoint(fishing.TransformPoint(RodGripRight));
                    Vector3 buttInItem = item.InverseTransformPoint(fishing.TransformPoint(RodGripLeft));
                    Quaternion rot = Quaternion.LookRotation(dir, U) * Quaternion.Inverse(fishingInItem);
                    Vector3 pos = grip - rot * Vector3.Scale(gripInItem, s);
                    // The cast: the game turns the rod back over the shoulder and whips it forward; here it turns about the
                    // hands rather than about a point out in front of them.
                    Spin(ref pos, ref rot, grip, R, spin);
                    Place(ref r, pos, rot);

                    Vector3 axisInItem = fishingInItem * Vector3.forward;
                    r.R = Bar(pos, rot, s, gripInItem, axisInItem);
                    r.L = Bar(pos, rot, s, buttInItem, axisInItem);
                    if (line > 0.01f && p.RodSpinner != null)
                    {
                        Vector3 knob = pos + rot * Vector3.Scale(item.InverseTransformPoint(p.RodSpinner.TransformPoint(RodReelKnob)), s);
                        Quaternion rodRot = rot * fishingInItem;
                        var reel = new HandGrip { On = true, Palm = knob, Normal = rodRot * Vector3.right, Finger = rodRot * Vector3.forward };
                        r.L = new HandGrip
                        {
                            On = true,
                            Palm = Vector3.Lerp(r.L.Palm, reel.Palm, line),
                            Normal = Vector3.Slerp(r.L.Normal, reel.Normal, line),
                            Finger = Vector3.Slerp(r.L.Finger, reel.Finger, line),
                            Axis = line > 0.5f ? Vector3.zero : r.L.Axis,
                        };
                    }
                    break;
                }

                case HoldStyle.Reach:
                default:
                {
                    // Left where the game holds it: an oar in the water, a rod or chip log on its line. The
                    // hands go to the grips and reach as far as the arms allow.
                    r.Repose = false;
                    Transform frame = p.ReachFrame != null ? p.ReachFrame : item;
                    Vector3 lr, ll, along;
                    if (p.Rod) { lr = new Vector3(0.28f, -0.66f, -0.74f); ll = new Vector3(0.29f, -0.30f, -0.42f); along = ll - lr; }
                    else if (p.ChipLog) { lr = new Vector3(0f, -0.28f, 0f); ll = new Vector3(0f, 0.42f, 0f); along = Vector3.up; }
                    else if (p.Anchor) { lr = new Vector3(c.x + 0.05f, b.max.y - 0.08f, c.z); ll = new Vector3(c.x - 0.05f, b.max.y - 0.08f, c.z); along = Vector3.right; }
                    else { lr = new Vector3(0f, 0.30f, 0f); ll = new Vector3(0f, 0.70f, 0f); along = Vector3.up; }
                    Vector3 axisW = frame.TransformDirection(along).normalized;
                    r.R = new HandGrip { On = true, Palm = frame.TransformPoint(lr), Normal = -R, Finger = F, Axis = axisW };
                    r.L = new HandGrip { On = true, Palm = frame.TransformPoint(ll), Normal = R, Finger = F, Axis = axisW };
                    break;
                }
            }

            if (r.Repose && p.Style != HoldStyle.Broom && p.Style != HoldStyle.Rod) KeepAboveDeck(ref r, b, s, f.Feet.y);
        }

        // Points on the shown fishing rod, in its 'fishing' frame, and on its reel handle, in the spinner's frame.
        private static readonly Vector3 RodGripRight = new Vector3(0f, 0.245f, -0.66f);
        private static readonly Vector3 RodGripLeft = new Vector3(0f, 0.25f, -1.2f);
        private static readonly Vector3 RodReelKnob = new Vector3(-0.07f, 0f, 0.1f);

        private static readonly AccessTools.FieldRef<ShipItemFishingRod, ConfigurableJoint> RodJoint =
            AccessTools.FieldRefAccess<ShipItemFishingRod, ConfigurableJoint>("bobberJoint");
        private static readonly AccessTools.FieldRef<ShipItemFishingRod, float> RodMinLength =
            AccessTools.FieldRefAccess<ShipItemFishingRod, float>("minLength");
        private static readonly AccessTools.FieldRef<ShipItemFishingRod, RopeEffect> RodRope =
            AccessTools.FieldRefAccess<ShipItemFishingRod, RopeEffect>("rope");

        /// <summary>
        /// Whether a rod has its line out: the game's own line length past its reeled-in minimum, or, for a crewmate's
        /// rod whose line length is not synced, the bobber well away from the tip.
        /// </summary>
        private static bool RodLineOut(ShipItemFishingRod rod, Transform tip)
        {
            var joint = RodJoint(rod);
            if (joint == null) return false;
            if (joint.linearLimit.limit > RodMinLength(rod) + 0.3f) return true;
            return tip != null && Vector3.Distance(joint.transform.position, tip.position) > 2f;
        }

        /// <summary>A fishing rod's line end on the rod and the object that draws its line, for keeping the line on a rod drawn in the hands.</summary>
        internal static bool RodLine(Transform item, out Transform tip, out Transform line)
        {
            tip = line = null;
            var rod = item != null ? item.GetComponent<ShipItemFishingRod>() : null;
            if (rod == null) return false;
            var p = Profile(item);
            var rope = RodRope(rod);
            if (p == null || p.RodTip == null || rope == null) return false;
            tip = p.RodTip;
            line = rope.transform;
            return true;
        }

        /// <summary>How far in front of the frame's chest point (the shoulder line) the front of the ribs is, in meters.</summary>
        private const float ChestFront = 0.12f;

        /// <summary>
        /// The holder's own turn of the item, in degrees about the view's right axis. The local player's is read
        /// straight from heldRotationOffset. A remote body has none to read, so the pitch of the synced item
        /// against the view estimated from the streamed look stands in, faded out below 25 degrees because that
        /// estimate is only so good. A big item's hold is not built from the view, so a remote one reads zero.
        /// </summary>
        private static float PlayerSpin(HoldProfile p, Quaternion rel, BodyFrame f)
        {
            if (p.Item == null) return 0f;
            if (f.HasPointer) return Mathf.DeltaAngle(0f, p.Item.heldRotationOffset);
            if (p.Style == HoldStyle.Big) return 0f;
            Vector3 fw = rel * Vector3.forward;
            float pitch = Mathf.Atan2(-fw.y, fw.z) * Mathf.Rad2Deg;
            return pitch * Mathf.InverseLerp(10f, 25f, Mathf.Abs(pitch));
        }

        /// <summary>Turn an item's pose by <paramref name="degrees"/> about <paramref name="axis"/> through <paramref name="pivot"/>.</summary>
        private static void Spin(ref Vector3 pos, ref Quaternion rot, Vector3 pivot, Vector3 axis, float degrees)
        {
            if (Mathf.Abs(degrees) < 0.01f) return;
            Quaternion q = Quaternion.AngleAxis(degrees, axis);
            pos = pivot + q * (pos - pivot);
            rot = q * rot;
        }

        /// <summary>
        /// Keep a sheet being read (a chart or a scroll) below the chin and clear of the body: lowered until its
        /// top edge is under the chin, then moved out until its nearest edge is in front of the ribs. A chart is
        /// 80 cm tall, so held at chest height its top edge reaches the hat.
        /// </summary>
        private static void FitBelowChin(ref Vector3 pos, Quaternion rot, Bounds b, Vector3 scale, BodyFrame f)
        {
            Vector3 c = b.center, e = b.extents;
            float top = float.MinValue, near = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x), c.y + ((i & 2) == 0 ? -e.y : e.y), c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 w = pos + rot * Vector3.Scale(corner, scale);
                top = Mathf.Max(top, w.y);
                near = Mathf.Min(near, Vector3.Dot(w - f.Chest, f.Forward));
            }
            float chin = f.Mouth.y - 0.08f;
            if (top > chin) pos += Vector3.down * (top - chin);
            float clear = ChestFront + 0.04f;
            if (near < clear) pos += f.Forward * (clear - near);
        }

        /// <summary>
        /// Where a cup's handle is, item-local, or zero for a cup without one. Mesh data cannot be read at
        /// runtime, so this works from the bounds: every vanilla cup is modeled round its own origin, so its body
        /// reaches the same distance out on every side and a handle is the one side that reaches clearly further
        /// (the metal mugs 10.2 cm on -X against 6.9 cm elsewhere, the wooden mug 10.5 against 7.2, the clay mug
        /// has none).
        /// </summary>
        private static Vector3 CupHandle(Bounds b)
        {
            float[] reach = { b.max.x, -b.min.x, b.max.z, -b.min.z };
            Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            int best = 0;
            float body = reach[0];
            for (int i = 1; i < 4; i++)
            {
                if (reach[i] > reach[best]) best = i;
                body = Mathf.Min(body, reach[i]);
            }
            if (reach[best] < body + 0.02f) return Vector3.zero;
            // The fist closes round the middle of the handle's thickness, a centimeter in from its outer edge.
            return dirs[best] * (reach[best] - 0.01f) + Vector3.up * (b.center.y + 0.005f);
        }

        /// <summary>The item's local axis, with sign, that <paramref name="rot"/> points nearest world up.</summary>
        private static Vector3 UpAxis(Quaternion rot)
        {
            Vector3 best = Vector3.up;
            float bestDot = float.MinValue;
            Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int i = 0; i < axes.Length; i++)
            {
                float d = Vector3.Dot(rot * axes[i], Vector3.up);
                if (d > bestDot) { bestDot = d; best = axes[i]; }
            }
            return best;
        }

        /// <summary>
        /// Lift a redrawn item, and the hands on it, until its lowest point is above the deck the body stands on.
        /// Whatever the bounds or the pose say, nothing carried ends up in the floor.
        /// </summary>
        private static void KeepAboveDeck(ref ItemPoseResult r, Bounds b, Vector3 scale, float deckY)
        {
            Vector3 sc = Vector3.Scale(scale, r.ScaleMul);
            Vector3 c = b.center, e = b.extents;
            float lowest = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x), c.y + ((i & 2) == 0 ? -e.y : e.y), c.z + ((i & 4) == 0 ? -e.z : e.z));
                lowest = Mathf.Min(lowest, (r.Pos + r.Rot * Vector3.Scale(corner, sc)).y);
            }
            float floor = deckY + 0.04f;
            if (lowest >= floor) return;
            Vector3 lift = Vector3.up * (floor - lowest);
            r.Pos += lift;
            if (r.R.On) r.R.Palm += lift;
            if (r.L.On) r.L.Palm += lift;
        }

        private static void Place(ref ItemPoseResult r, Vector3 pos, Quaternion rot)
        {
            r.Repose = true;
            r.Pos = pos;
            r.Rot = rot;
        }

        private static HandGrip Grip(Vector3 pos, Quaternion rot, Vector3 scale, Vector3 local, Vector3 normalLocal, Vector3 fingerLocal)
        {
            return new HandGrip
            {
                On = true,
                Palm = pos + rot * Vector3.Scale(local, scale),
                Normal = (rot * normalLocal).normalized,
                Finger = (rot * fingerLocal).normalized,
            };
        }

        /// <summary>A fist round a bar that runs along <paramref name="axisLocal"/> in the item's frame.</summary>
        private static HandGrip Bar(Vector3 pos, Quaternion rot, Vector3 scale, Vector3 local, Vector3 axisLocal)
        {
            return new HandGrip
            {
                On = true,
                Palm = pos + rot * Vector3.Scale(local, scale),
                Axis = (rot * axisLocal).normalized,
                Finger = rot * Vector3.forward,
                Normal = rot * Vector3.up,
            };
        }

        /// <summary>Half-extent of a local box, turned by <paramref name="rot"/>, along a world direction.</summary>
        private static float Project(Quaternion rot, Vector3 e, Vector3 dir)
        {
            return Mathf.Abs(Vector3.Dot(rot * Vector3.right, dir)) * e.x
                 + Mathf.Abs(Vector3.Dot(rot * Vector3.up, dir)) * e.y
                 + Mathf.Abs(Vector3.Dot(rot * Vector3.forward, dir)) * e.z;
        }

        /// <summary>
        /// How far an item is toward the mouth, 0 to 1, read from the game's own hold distance: drinking, eating
        /// and smoking all pull the item in by animating that value. A remote body has no hold distance to read,
        /// so its item's distance from the head stands in.
        /// </summary>
        private static float UseProgress(HoldProfile p, Vector3 floatPos, BodyFrame f)
        {
            var it = p.Item;
            if (it == null) return 0f;
            float initial, target;
            if (p.Bottle != null)
            {
                initial = BottleInitial(p.Bottle);
                target = (Mathf.Abs(p.Bottle.dist - 0.22f) > 0.001f && p.Bottle.dist > 0f) ? p.Bottle.dist : 0.25f;
                if (p.Bottle.amount == 9f && p.Bottle.GetCapacity() <= 10f) target *= 2f;
            }
            else if (p.Food != null) { initial = FoodInitial(p.Food); target = 0.05f; }
            else if (p.Pipe != null) { initial = PipeInitial(p.Pipe); target = 0.25f; }
            else return 0f;

            if (initial <= 0f) initial = it.holdDistance;
            if (initial <= target + 0.02f) return 0f;
            float current;
            if (f.HasPointer) current = it.holdDistance;
            else
            {
                if (p.Style == HoldStyle.Big) return 0f;   // a carried barrel sits close to a remote head anyway
                current = Vector3.Distance(floatPos, f.Head);
            }
            return Mathf.Clamp01(Mathf.InverseLerp(initial, target, current));
        }
    }

    /// <summary>
    /// While item poses are on, a barrel goes back to being carried once you stop drinking from it. ShipItemBottle.OnAltHeld
    /// clears `big` so the barrel can be tipped at the mouth like a bottle, and only OnDrop sets it again, so until it was put
    /// down the game held the barrel upright in front of the face while the body still carried it. With item poses off the
    /// game's own handling stands.
    /// </summary>
    [HarmonyPatch(typeof(ShipItemBottle))]
    internal static class BarrelCarryPatches
    {
        // The one barrel the local player is drinking from. Only the held item receives OnAltHeld.
        private static ShipItemBottle _drinking;

        private static bool PoseInUse()
        {
            return InteractionTuning.Enabled != null && InteractionTuning.Enabled.Value
                && HeldToolPose.Mode != null && HeldToolPose.Mode.Value == HeldPoseMode.ItemInHand;
        }

        [HarmonyPrefix]
        [HarmonyPatch("OnAltHeld", new System.Type[0])]
        private static void BeforeDrink(ShipItemBottle __instance)
        {
            if (__instance.big && __instance.GetCapacity() > 10f && PoseInUse()) _drinking = __instance;
        }

        [HarmonyPostfix]
        [HarmonyPatch("ExtraLateUpdate")]
        private static void AfterLateUpdate(ShipItemBottle __instance)
        {
            // Runs for every bottle every frame: one reference compare for all but the barrel being drunk from.
            if (!ReferenceEquals(__instance, _drinking) || __instance.IsDrinking()) return;
            _drinking = null;
            // Still in hand: carry it again. Dropped: OnDrop has already set it. Checked again in case poses were
            // switched off mid-drink.
            if (__instance.held != null && PoseInUse()) __instance.big = true;
        }
    }
}
