using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using PsychoticLab;
using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// What one character looks like, built on the Synty POLYGON modular character system that Sailwind
    /// already ships.
    ///
    /// WHY THIS IS EVEN POSSIBLE. Every shopkeeper NPC in the game is a "Modular NPC" carrying a LIVE
    /// PsychoticLab.CharacterCustomizer, with the ENTIRE part library still present as disabled child
    /// objects - the editor-time Apply() bake that would have destroyed the unused parts was never run on
    /// any shipped NPC. So the body clone this mod makes has every head, hair, torso and leg variant sitting
    /// inside it, switched off. Roughly 720 skinned meshes across 41 group nodes.
    ///
    /// WHY EVERY BODY WOULD OTHERWISE LOOK IDENTICAL, and how that becomes the mechanism. BodyTemplate.Strip
    /// does NOT remove CharacterCustomizer, and every body path calls SetActive(true) on the
    /// clone. That fires CharacterCustomizer.Start(), which runs BuildLists() + ClearItems() + UpdateModel()
    /// and rebuilds the model from the SOURCE shopkeeper's serialized indices - overwriting anything set
    /// beforehand and stamping every player with the face of whichever shopkeeper happened to load first.
    /// The selection fields are all public ints, so the fix is not to fight that pass but to feed it: write
    /// the indices BEFORE activation and vanilla's own Start() applies the whole outfit, materials included.
    ///
    /// THEREFORE: Apply() MUST be called while the clone is still inactive. Calling it afterwards does
    /// nothing visible until something else re-runs UpdateModel.
    ///
    /// SAFETY. Indices arriving from the network are never trusted to index anything. They are wrapped at
    /// apply time into the LIVE count of wearable parts in the group on THIS machine (NormalizeValue), and
    /// every index is then clamped to its list (SanitizeIndices), which is the only count that can actually
    /// be indexed here - deliberately not against a compiled-in table, because the body template is cloned
    /// from whichever NPC loaded and a hardcoded count could drift past it. An out-of-range value wraps
    /// around the group's variants, 0 is "wear nothing" only on the slots that allow it and wraps to the
    /// last variant elsewhere, and an unknown slot name is ignored, so the worst case is an unexpected look
    /// rather than an invisible or T-posing one.
    /// </summary>
    public struct PlayerAppearance
    {
        /// <summary>One selectable group. Getter/setter are direct field access on the vanilla component.</summary>
        public sealed class Slot
        {
            public readonly string Key;          // stable wire/config token - NEVER renamed once shipped
            public readonly string Label;        // shown in the UI
            public readonly Func<CharacterCustomizer, int> Get;
            public readonly Action<CharacterCustomizer, int> Set;
            // Live variant list on THIS machine's clone. The customizer keeps male/female/allGender part
            // lists as serialized List<GameObject>; counting them is how an index gets validated.
            public readonly Func<CharacterCustomizer, List<GameObject>> List;
            /// <summary>Whether "wear nothing" is a legal choice. TRUE for accessories (a bald sailor with
            /// no hat is a person); FALSE for anatomy, where it deletes the body part and leaves a floating
            /// head or an invisible character.</summary>
            public readonly bool AllowsNone;
            public Slot(string key, string label, Func<CharacterCustomizer, int> get,
                Action<CharacterCustomizer, int> set, Func<CharacterCustomizer, List<GameObject>> list,
                bool allowsNone)
            { Key = key; Label = label; Get = get; Set = set; List = list; AllowsNone = allowsNone; }
        }

        // Tier 1 slot set: the groups that visibly change WHO the character is, in the order a player would
        // reasonably work down a character screen. The remaining vanilla fields (per-limb arms/hands, the
        // attachment points) are deliberately left on whatever the source shopkeeper had - they are mostly
        // armour hardpoints, and exposing 44 sliders would bury the ones that matter.
        //
        // Male and female variants of the same concept share ONE slot: which underlying field is written
        // depends on the Gender slot, so a player switching gender keeps a coherent outfit instead of
        // carrying indices into a list of a different length.
        public static readonly Slot[] Slots = new[]
        {
            new Slot("gender", "Gender",
                c => c.isFemale ? 2 : 1,
                (c, v) => c.isFemale = v == 2,
                c => null, allowsNone: false),               // 2 fixed options, no list to count
            new Slot("head", "Head",
                c => c.isFemale ? c.femaleHeadAllElements : c.maleHeadAllElements,
                (c, v) => { if (c.isFemale) c.femaleHeadAllElements = v; else c.maleHeadAllElements = v; },
                c => Group(c).headAllElements, allowsNone: false),   // anatomy: none = invisible player
            new Slot("hair", "Hair",
                c => c.allGenderAll_Hair,
                (c, v) => c.allGenderAll_Hair = v,
                c => c.allGender != null ? c.allGender.all_Hair : null, allowsNone: true),   // bald is a look
            new Slot("eyebrow", "Eyebrows",
                c => c.isFemale ? c.femaleEyebrow : c.maleEyebrow,
                (c, v) => { if (c.isFemale) c.femaleEyebrow = v; else c.maleEyebrow = v; },
                c => Group(c).eyebrow, allowsNone: false),
            new Slot("facialhair", "Facial hair",
                c => c.maleFacialHair,
                (c, v) => c.maleFacialHair = v,
                c => c.male != null ? c.male.facialHair : null, allowsNone: false),   // male-only in the vanilla rig
            // Hats live in headCoverings_Base_Hair, NOT all_Head_Attachment. The latter holds two raw
            // import folders ("Hair", "Helmet") with no meshes of their own - every shopkeeper in the game
            // ships with it set to -1, and pointing the UI at it produced a thicket of eleven overlapping
            // helmet crests plus an NRE inside vanilla's ActivateItem. headCoverings_Base_Hair is the
            // variant set meant to be worn WITH hair, which is the sane default for a crew of sailors.
            new Slot("headgear", "Hat",
                c => c.allGenderHeadCoverings_Base_Hair,
                (c, v) => c.allGenderHeadCoverings_Base_Hair = v,
                c => c.allGender != null ? c.allGender.headCoverings_Base_Hair : null, allowsNone: true),   // bare-headed is a look
            new Slot("torso", "Torso",
                c => c.isFemale ? c.femaleTorso : c.maleTorso,
                (c, v) => { if (c.isFemale) c.femaleTorso = v; else c.maleTorso = v; },
                c => Group(c).torso, allowsNone: false),     // anatomy: none left a floating head
            new Slot("hips", "Hips",
                c => c.isFemale ? c.femaleHips : c.maleHips,
                (c, v) => { if (c.isFemale) c.femaleHips = v; else c.maleHips = v; },
                c => Group(c).hips, allowsNone: false),
            new Slot("legs", "Legs",
                c => c.isFemale ? c.femaleLeg_Right : c.maleLeg_Right,
                (c, v) => {
                    // Both legs move together - a mismatched pair reads as a bug, never as a choice.
                    if (c.isFemale) { c.femaleLeg_Right = v; c.femaleLeg_Left = v; }
                    else { c.maleLeg_Right = v; c.maleLeg_Left = v; }
                },
                c => Group(c).leg_Right, allowsNone: false),
        };

        /// <summary>The male or female part-list bundle, whichever this customizer is currently set to.
        /// Never null - an empty stand-in keeps the slot table free of null checks, and a slot whose list
        /// is empty is simply skipped (count 0) rather than crashing the build.</summary>
        private static CharacterObjectGroups Group(CharacterCustomizer c)
        {
            var g = c.isFemale ? c.female : c.male;
            return g ?? _emptyGroups;
        }
        private static readonly CharacterObjectGroups _emptyGroups = new CharacterObjectGroups();

        public static int SlotCount { get { return Slots.Length; } }

        // ---- colors ------------------------------------------------------------------------------------
        // Every Synty part shares ONE material (shader SyntyStudios/CustomCharacter) whose color properties
        // decide what the cloth, leather, metal, hair and skin regions of the atlas look like. Each body gets
        // its own copy of the template's material and these slots write into it. Value 0 on a slot means
        // "as the cloned NPC wears it", which is what every appearance string from before colors existed
        // decodes to, so nobody's look changes on update until they pick something.

        public struct ColorOption
        {
            public readonly string Name;
            public readonly Color Color;
            public ColorOption(string name, float r, float g, float b) { Name = name; Color = new Color(r, g, b, 1f); }
        }

        public sealed class ColorSlot
        {
            public readonly string Key;           // stable wire/config token - NEVER renamed once shipped
            public readonly string Label;
            public readonly string[] Props;       // every property the slot writes; restored from the template for value 0
            public readonly ColorOption[] Palette;
            public readonly Action<Material, Color> Write;
            public ColorSlot(string key, string label, string[] props, ColorOption[] palette, Action<Material, Color> write)
            { Key = key; Label = label; Props = props; Palette = palette; Write = write; }
        }

        static readonly ColorOption[] ClothPalette =
        {
            new ColorOption("White", 0.92f, 0.90f, 0.85f), new ColorOption("Cream", 0.90f, 0.84f, 0.68f),
            new ColorOption("Sand", 0.80f, 0.70f, 0.52f),  new ColorOption("Tan", 0.66f, 0.52f, 0.36f),
            new ColorOption("Brown", 0.45f, 0.32f, 0.22f), new ColorOption("Dark brown", 0.28f, 0.20f, 0.14f),
            new ColorOption("Red", 0.70f, 0.18f, 0.16f),   new ColorOption("Maroon", 0.45f, 0.12f, 0.14f),
            new ColorOption("Orange", 0.85f, 0.45f, 0.15f), new ColorOption("Mustard", 0.80f, 0.65f, 0.20f),
            new ColorOption("Green", 0.25f, 0.50f, 0.25f), new ColorOption("Olive", 0.40f, 0.42f, 0.22f),
            new ColorOption("Teal", 0.18f, 0.48f, 0.48f),  new ColorOption("Sky", 0.49f, 0.70f, 0.83f),
            new ColorOption("Blue", 0.25f, 0.42f, 0.70f),  new ColorOption("Navy", 0.14f, 0.20f, 0.36f),
            new ColorOption("Purple", 0.42f, 0.25f, 0.50f), new ColorOption("Grey", 0.55f, 0.55f, 0.55f),
            new ColorOption("Charcoal", 0.25f, 0.25f, 0.27f), new ColorOption("Black", 0.10f, 0.10f, 0.11f),
        };
        static readonly ColorOption[] LeatherPalette =
        {
            new ColorOption("Tan", 0.62f, 0.45f, 0.30f),   new ColorOption("Brown", 0.42f, 0.29f, 0.20f),
            new ColorOption("Dark brown", 0.28f, 0.19f, 0.13f), new ColorOption("Red-brown", 0.48f, 0.22f, 0.16f),
            new ColorOption("Black", 0.12f, 0.11f, 0.10f), new ColorOption("Grey", 0.40f, 0.38f, 0.36f),
            new ColorOption("Olive", 0.36f, 0.36f, 0.24f),
        };
        static readonly ColorOption[] MetalPalette =
        {
            new ColorOption("Steel", 0.63f, 0.62f, 0.56f), new ColorOption("Iron", 0.45f, 0.46f, 0.48f),
            new ColorOption("Bronze", 0.60f, 0.42f, 0.25f), new ColorOption("Brass", 0.72f, 0.58f, 0.28f),
            new ColorOption("Gold", 0.85f, 0.68f, 0.25f),  new ColorOption("Silver", 0.78f, 0.80f, 0.82f),
            new ColorOption("Black iron", 0.20f, 0.20f, 0.22f),
        };
        static readonly ColorOption[] HairPalette =
        {
            new ColorOption("Black", 0.10f, 0.09f, 0.08f), new ColorOption("Dark brown", 0.22f, 0.15f, 0.10f),
            new ColorOption("Brown", 0.36f, 0.24f, 0.15f), new ColorOption("Chestnut", 0.45f, 0.28f, 0.16f),
            new ColorOption("Auburn", 0.52f, 0.24f, 0.14f), new ColorOption("Red", 0.62f, 0.22f, 0.12f),
            new ColorOption("Ginger", 0.75f, 0.40f, 0.18f), new ColorOption("Blond", 0.80f, 0.65f, 0.40f),
            new ColorOption("Light blond", 0.89f, 0.78f, 0.55f), new ColorOption("Platinum", 0.90f, 0.86f, 0.75f),
            new ColorOption("Grey", 0.58f, 0.58f, 0.58f),  new ColorOption("White", 0.88f, 0.88f, 0.86f),
        };
        static readonly ColorOption[] SkinPalette =
        {
            new ColorOption("Pale", 0.96f, 0.84f, 0.74f),  new ColorOption("Fair", 1.00f, 0.80f, 0.68f),
            new ColorOption("Light", 0.90f, 0.72f, 0.58f), new ColorOption("Tan", 0.80f, 0.62f, 0.46f),
            new ColorOption("Olive", 0.72f, 0.55f, 0.40f), new ColorOption("Brown", 0.58f, 0.40f, 0.28f),
            new ColorOption("Dark brown", 0.42f, 0.28f, 0.18f), new ColorOption("Deep", 0.28f, 0.18f, 0.12f),
        };

        // Skin before hair: the stubble tint is blended from the hair color toward the skin already set.
        public static readonly ColorSlot[] ColorSlots = new[]
        {
            new ColorSlot("c_skin", "Skin", new[] { "_Color_Skin", "_Color_Scar" }, SkinPalette,
                (m, c) => { SetColor(m, "_Color_Skin", c); SetColor(m, "_Color_Scar", new Color(c.r * 0.95f, c.g * 0.72f, c.b * 0.66f)); }),
            new ColorSlot("c_hair", "Hair color", new[] { "_Color_Hair", "_Color_Stubble" }, HairPalette,
                (m, c) =>
                {
                    SetColor(m, "_Color_Hair", c);
                    var skin = m.HasProperty("_Color_Skin") ? m.GetColor("_Color_Skin") : c;
                    SetColor(m, "_Color_Stubble", Color.Lerp(c, skin, 0.5f));
                }),
            new ColorSlot("c_primary", "Cloth", new[] { "_Color_Primary" }, ClothPalette,
                (m, c) => SetColor(m, "_Color_Primary", c)),
            new ColorSlot("c_secondary", "Trim", new[] { "_Color_Secondary" }, ClothPalette,
                (m, c) => SetColor(m, "_Color_Secondary", c)),
            new ColorSlot("c_leather", "Leather", new[] { "_Color_Leather_Primary", "_Color_Leather_Secondary" }, LeatherPalette,
                (m, c) => { SetColor(m, "_Color_Leather_Primary", c); SetColor(m, "_Color_Leather_Secondary", Scale(c, 1.2f)); }),
            new ColorSlot("c_metal", "Metal", new[] { "_Color_Metal_Primary", "_Color_Metal_Secondary", "_Color_Metal_Dark" }, MetalPalette,
                (m, c) => { SetColor(m, "_Color_Metal_Primary", c); SetColor(m, "_Color_Metal_Secondary", Scale(c, 0.8f)); SetColor(m, "_Color_Metal_Dark", Scale(c, 0.45f)); }),
        };

        public static int ColorSlotCount { get { return ColorSlots.Length; } }

        static Color Scale(Color c, float k) { return new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), 1f); }

        /// <summary>Write RGB, keeping the alpha the material already had: on this shader some alphas gate an effect.</summary>
        static void SetColor(Material m, string prop, Color c)
        {
            if (!m.HasProperty(prop)) return;
            var old = m.GetColor(prop);
            m.SetColor(prop, new Color(c.r, c.g, c.b, old.a));
        }

        /// <summary>The palette entry name for the screen. 0 is described by the nearest palette entry.</summary>
        public static string DescribeColor(int colorSlot, int value)
        {
            if (colorSlot < 0 || colorSlot >= ColorSlots.Length) return "";
            var p = ColorSlots[colorSlot].Palette;
            if (value <= 0 || value > p.Length)
            {
                int near = NearestPaletteIndex(colorSlot);
                return near > 0 ? p[near - 1].Name : "Default";
            }
            return p[value - 1].Name;
        }

        /// <summary>
        /// The palette entry closest to what the cloned NPC wears in this slot, 1-based; 0 if that cannot be
        /// known yet. The screen resolves a 0 to this so the player always sees a named color and steps
        /// from where they actually are, and a 0 in a saved string still means the NPC's exact colors.
        /// </summary>
        public static int NearestPaletteIndex(int colorSlot)
        {
            if (colorSlot < 0 || colorSlot >= ColorSlots.Length) return 0;
            var slot = ColorSlots[colorSlot];
            var m = BodyTemplate.SharedMaterial;
            if (m == null || slot.Props.Length == 0 || !m.HasProperty(slot.Props[0])) return 0;
            var c = m.GetColor(slot.Props[0]);
            int best = 0; float bestD = float.MaxValue;
            for (int i = 0; i < slot.Palette.Length; i++)
            {
                var q = slot.Palette[i].Color;
                float d = (q.r - c.r) * (q.r - c.r) + (q.g - c.g) * (q.g - c.g) + (q.b - c.b) * (q.b - c.b);
                if (d < bestD) { bestD = d; best = i + 1; }
            }
            return best;
        }

        /// <summary>Per-slot chosen index. Null/short arrays are treated as all-zero (the default sailor).</summary>
        public byte[] Values;

        /// <summary>Per-color-slot palette index, 0 = as the cloned NPC wears it. Null/short = all zero.</summary>
        public byte[] Colors;

        public byte GetColor(int i)
        {
            return (Colors != null && i >= 0 && i < Colors.Length) ? Colors[i] : (byte)0;
        }

        public void SetColor(int i, byte value)
        {
            if (Colors == null || Colors.Length < ColorSlots.Length)
            {
                var next = new byte[ColorSlots.Length];
                if (Colors != null) Array.Copy(Colors, next, Math.Min(Colors.Length, next.Length));
                Colors = next;
            }
            if (i >= 0 && i < Colors.Length) Colors[i] = value;
        }

        public static PlayerAppearance Default()
        {
            return new PlayerAppearance { Values = new byte[Slots.Length], Colors = new byte[ColorSlots.Length] };
        }

        public byte this[int i]
        {
            get { return (Values != null && i >= 0 && i < Values.Length) ? Values[i] : (byte)0; }
            set
            {
                if (Values == null || Values.Length < Slots.Length) Resize();
                if (i >= 0 && i < Values.Length) Values[i] = value;
            }
        }

        private void Resize()
        {
            var next = new byte[Slots.Length];
            if (Values != null) Array.Copy(Values, next, Math.Min(Values.Length, next.Length));
            Values = next;
        }

        /// <summary>
        /// How many variants this machine actually has for a slot. Counted from the LIVE group children,
        /// because that is the only list an index can be applied against here. 0 means "not countable" and
        /// callers should treat the slot as unavailable rather than guessing a range.
        /// </summary>
        public static int VariantCount(CharacterCustomizer c, int slotIndex)
        {
            if (c == null || slotIndex < 0 || slotIndex >= Slots.Length) return 0;
            if (Slots[slotIndex].Key == "gender") return 2;
            try
            {
                var list = Slots[slotIndex].List(c);
                if (list == null) return 0;
                // Count only entries that are actually WEARABLE. Some groups contain raw import folders
                // with no renderer of their own; vanilla dereferences that renderer unguarded, and even
                // with our guard in place a folder is not a thing a player can meaningfully choose. Only
                // entries carrying a mesh are offered, and the index space is theirs alone.
                int n = 0;
                for (int i = 0; i < list.Count; i++)
                    if (IsWearable(list[i])) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Force every group the UI does not expose into a DETERMINISTIC state, so an appearance string
        /// means the same thing on every machine.
        ///
        /// Without this, unexposed groups keep whatever the locally-cloned shopkeeper wore - and each
        /// machine clones whichever shopkeeper happened to load first - so two players would see the same
        /// crewmate wearing different gloves, pauldrons and sleeves. That silently defeats the point of
        /// putting appearance on the wire.
        ///
        /// The split matters: pure ATTACHMENTS (pauldrons, chest/back kit, knee pads, elf ears) go to -1,
        /// "wear nothing", which is the state every shipped NPC uses. LIMBS do not - setting an arm or a
        /// hand to -1 deletes it and leaves a character with stumps - so those follow the TORSO choice,
        /// which is also how Synty rigs keep sleeve and glove styling continuous with the jacket.
        /// </summary>
        private void PinUnexposedGroups(CharacterCustomizer c)
        {
            var g = Group(c);

            // Accessories: nothing.
            c.allGenderAll_Head_Attachment = -1;          // the folder group - never valid to select
            c.allGenderHeadCoverings_No_FacialHair = -1;  // alternates for the hat slot we DO expose
            c.allGenderHeadCoverings_No_Hair = -1;
            c.allGenderChest_Attachment = -1;
            c.allGenderBack_Attachment = -1;
            c.allGenderShoulder_Attachment_Right = -1;
            c.allGenderShoulder_Attachment_Left = -1;
            c.allGenderElbow_Attachment_Right = -1;
            c.allGenderElbow_Attachment_Left = -1;
            c.allGenderHips_Attachment = -1;
            c.allGenderKnee_Attachement_Right = -1;
            c.allGenderKnee_Attachement_Left = -1;
            c.allGenderElf_Ear = -1;

            // The alternate head variant: we drive headAllElements, so this one must stand down or two
            // heads are enabled at once.
            if (c.isFemale) c.femaleHeadNoElements = -1; else c.maleHeadNoElements = -1;

            // Limbs: match the torso so sleeves and gloves stay stylistically continuous, and stay equal
            // across machines. Each is wrapped into its OWN list length - the groups are not all the same
            // size, so reusing the torso number raw would go out of range.
            int torso = c.isFemale ? c.femaleTorso : c.maleTorso;
            if (torso < 0) torso = 0;
            SetLimb(c, g.arm_Upper_Right, torso, v => { if (c.isFemale) c.femaleArm_Upper_Right = v; else c.maleArm_Upper_Right = v; });
            SetLimb(c, g.arm_Upper_Left,  torso, v => { if (c.isFemale) c.femaleArm_Upper_Left  = v; else c.maleArm_Upper_Left  = v; });
            SetLimb(c, g.arm_Lower_Right, torso, v => { if (c.isFemale) c.femaleArm_Lower_Right = v; else c.maleArm_Lower_Right = v; });
            SetLimb(c, g.arm_Lower_Left,  torso, v => { if (c.isFemale) c.femaleArm_Lower_Left  = v; else c.maleArm_Lower_Left  = v; });
            SetLimb(c, g.hand_Right,      torso, v => { if (c.isFemale) c.femaleHand_Right      = v; else c.maleHand_Right      = v; });
            SetLimb(c, g.hand_Left,       torso, v => { if (c.isFemale) c.femaleHand_Left       = v; else c.maleHand_Left       = v; });
        }

        /// <summary>
        /// Clamp every selection index on the customizer into the range of the list it indexes. The fields
        /// and lists pair up by name (maleTorso / male.torso, allGenderAll_Hair / allGender.all_Hair), so
        /// this covers fields this mod never heard of, which is the point: it is the guard for a rig whose
        /// part library differs from the one the mod was written against. The body groups (male, female)
        /// are indexed unguarded by vanilla, so they are never left below zero; the allGender accessories
        /// are guarded and -1 stays legal there.
        /// </summary>
        private static void SanitizeIndices(CharacterCustomizer c)
        {
            SanitizeGroup(c, "male", c.male, false);
            SanitizeGroup(c, "female", c.female, false);
            SanitizeGroup(c, "allGender", c.allGender, true);
        }

        private static void SanitizeGroup(CharacterCustomizer c, string prefix, object group, bool allowsNone)
        {
            if (group == null) return;
            var gt = group.GetType();
            foreach (var f in typeof(CharacterCustomizer).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType != typeof(int) || !f.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string rest = f.Name.Substring(prefix.Length);
                if (rest.Length == 0) continue;
                string listName = char.ToLowerInvariant(rest[0]) + rest.Substring(1);
                var lf = gt.GetField(listName, BindingFlags.Public | BindingFlags.Instance);
                if (lf == null) continue;
                var list = lf.GetValue(group) as List<GameObject>;
                int count = list != null ? list.Count : 0;
                int v = (int)f.GetValue(c);
                int nv = v;
                if (count == 0) { if (allowsNone) nv = -1; }        // nothing to index; vanilla throws either way on a body group
                else if (v >= count) nv = count - 1;
                else if (v < 0 && !allowsNone) nv = 0;
                if (nv != v)
                {
                    f.SetValue(c, nv);
                    // The alternate head group is pinned to -1 on purpose and only indexed when its flag is
                    // set, so that one is routine; anything else is worth a line.
                    if (!f.Name.EndsWith("HeadNoElements", StringComparison.Ordinal))
                        Plugin.Log.LogInfo($"[PlayerModel] {f.Name} {v} is out of range for {count} part(s); using {nv}");
                }
            }
        }

        private static void SetLimb(CharacterCustomizer c, List<GameObject> list, int preferred, Action<int> set)
        {
            if (list == null || list.Count == 0) { set(-1); return; }
            // Never -1 for a limb: that would remove the arm or hand entirely.
            set(Mathf.Abs(preferred) % list.Count);
        }

        /// <summary>
        /// Fold a value into the legal range for a slot, wrapping at both ends. THE one place that decides
        /// what a slot's values mean, so the arrows, Randomize, the on-open tidy and Apply cannot disagree.
        ///
        /// 0 means "wear nothing" and exists only where AllowsNone. Making it universal was a real bug with
        /// two faces: picking None on Torso left a floating head and on Head made the player vanish
        /// entirely, and Gender - which has exactly two options - ended up cycling through THREE values, so
        /// the phantom None rendered as Male and going Female -> Male took two clicks.
        /// </summary>
        public static byte NormalizeValue(int slotIndex, int count, int value)
        {
            if (slotIndex < 0 || slotIndex >= Slots.Length || count <= 0) return 0;
            int lo = Slots[slotIndex].AllowsNone ? 0 : 1;   // 1-based when "nothing" is not on offer
            int span = count + 1 - lo;
            if (span <= 0) return (byte)lo;
            int v = (value - lo) % span;
            if (v < 0) v += span;
            return (byte)(v + lo);
        }

        private static bool IsWearable(GameObject go)
        {
            if (go == null) return false;
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null) return false;
            // (v0.3.0) A renderer is not the same thing as something to look at. One head entry carries a
            // SkinnedMeshRenderer with no geometry behind it, so it counted as a choice and gave the player
            // an invisible head at 23 of 23. Requiring actual vertices drops it and leaves 22 real heads,
            // and does so by what the entry IS rather than by hardcoding an index that would drift the
            // moment the game shipped a different part list.
            return smr.sharedMesh != null && smr.sharedMesh.vertexCount > 0;
        }

        /// <summary>
        /// Translate a UI/wire value into the index vanilla wants. Value 0 means NOTHING - vanilla spells
        /// that -1, which a byte cannot carry - and 1..n select the nth WEARABLE entry, skipping any
        /// non-mesh folders so our indices never drift from what the player was shown.
        /// </summary>
        private static int ToVanillaIndex(CharacterCustomizer c, int slotIndex, int value)
        {
            if (value <= 0) return -1;
            var list = Slots[slotIndex].List(c);
            if (list == null) return -1;
            int wanted = value;                    // 1-based among wearables
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsWearable(list[i])) continue;
                if (--wanted == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// Write these indices onto a body clone. MUST run while the clone is INACTIVE - see the class doc:
        /// vanilla's Start() is what actually rebuilds the mesh, and it reads these fields. Never throws.
        /// </summary>
        public void Apply(GameObject bodyClone)
        {
            if (bodyClone == null) return;
            try
            {
                var c = bodyClone.GetComponentInChildren<CharacterCustomizer>(true);
                if (c == null) return; // not a modular NPC (a mod-added or baked body) - leave it alone

                // Gender first: every other slot's target field and variant count depends on it, so writing
                // it late would clamp against the wrong list. But do NOT switch to female unless this rig
                // actually HAS female parts - gender is the one slot with no list of its own, and flipping
                // it redirects vanilla into female.* lists that an all-male NPC rig may leave empty,
                // producing a bodiless character rather than a different one.
                bool wantFemale = this[0] == 2;
                if (wantFemale && (c.female == null || c.female.torso == null || c.female.torso.Count == 0))
                    wantFemale = false;
                Slots[0].Set(c, wantFemale ? 2 : 1);

                for (int i = 1; i < Slots.Length; i++)
                {
                    int count = VariantCount(c, i);
                    // Nothing wearable in this group. Only write -1 where vanilla GUARDS it: UpdateModel
                    // indexes the BODY groups unguarded (male.eyebrow[maleEyebrow] and friends), so -1 on
                    // one of those throws mid-build and every part after it never activates. Leaving the
                    // source value alone is strictly safer than writing one that aborts the model.
                    if (count <= 0)
                    {
                        if (Slots[i].AllowsNone) Slots[i].Set(c, -1);
                        continue;
                    }

                    // Value space is 0 = nothing, 1..count = the nth wearable entry. WRAP rather than
                    // clamp-to-zero: the deterministic per-SteamId look draws from a wide range and most
                    // groups hold only a handful of variants, so clamping collapsed the majority of slots
                    // to one value and made crews look near-identical - exactly what that look exists to
                    // prevent. Wrapping keeps the variation and is equally safe.
                    int v = NormalizeValue(i, count, this[i]);
                    Slots[i].Set(c, ToVanillaIndex(c, i, v));
                }

                // Pin every group we do NOT expose to "nothing". Without this they keep whatever the local
                // machine's cloned shopkeeper happened to be wearing - so the same appearance string would
                // render differently on every client, which defeats the point of syncing it at all.
                PinUnexposedGroups(c);

                // Last: make every index legal for THIS rig. Vanilla's UpdateModel indexes the body groups
                // unguarded, and the moment one throws, every part after it never activates and the body is
                // left at Start's bare defaults. A rig with a different part library (another NPC prefab, a
                // game update) must never be able to do that.
                SanitizeIndices(c);

                ApplyColors(c);
            }
            catch (Exception e)
            {
                // An appearance is cosmetic; never let it be the reason a body fails to build.
                Plugin.Log.LogWarning("[Appearance] Could not apply appearance: " + e.Message);
            }
        }

        /// <summary>
        /// Restyle a body that is ALREADY ACTIVE, in place, with no destroy-and-rebuild.
        ///
        /// Vanilla's Start() does BuildLists() + ClearItems() + UpdateModel(); those are private, but the
        /// last two are all that a re-dress needs once the lists exist, and Harmony's Traverse reaches them
        /// (the same mechanism this codebase already uses for StartMenu's private fields).
        ///
        /// WHY THIS MATTERS RATHER THAN REBUILDING. The obvious implementation - destroy the clone and let
        /// the lazy builder make a new one - is wrong in three separate ways that all bite at once. The
        /// rebuild is throttled to ~1.5s, so a player clicking through variants gets a body that is absent
        /// more often than present. The retry gate is scaled Time.time, which is FROZEN while the pause
        /// menu holds timeScale at 0, so in a paused menu the rebuild may never happen at all. And each
        /// rebuild re-runs the leg-IK bind capture, which is documented as unsafe to re-run because it
        /// re-reads a foot rotation the IK has already written. Restyling in place avoids all three.
        /// Never throws: a cosmetic change must not disturb a session.
        /// </summary>
        public void ApplyLive(CharacterCustomizer c)
        {
            if (c == null) return;
            try
            {
                Apply(c.gameObject);
                var t = HarmonyLib.Traverse.Create(c);
                t.Method("ClearItems").GetValue();
                t.Method("UpdateModel").GetValue();
            }
            catch (Exception e)
            {
                // UNWRAP. Traverse invokes through reflection, so a throw inside vanilla arrives as
                // TargetInvocationException whose Message is the useless "Exception has been thrown by the
                // target of an invocation." That string appeared 40+ times in a live log - once per arrow
                // click, a 100% failure rate - while hiding the actual NullReferenceException inside
                // ActivateItem that was the real fault. Never let that happen twice.
                var inner = e is System.Reflection.TargetInvocationException && e.InnerException != null
                    ? e.InnerException : e;
                Plugin.Log.LogWarning("[Appearance] Live restyle failed: " + inner);
            }
        }

        /// <summary>
        /// The material this body's parts wear, private to this body. Null when colors cannot be applied:
        /// the template never got its own copy, so writing would recolor the live NPC it was cloned from.
        /// </summary>
        private static Material EnsureOwnMaterial(CharacterCustomizer c)
        {
            var shared = BodyTemplate.SharedMaterial;
            if (shared == null || c.mat == null) return null;
            if (c.mat == shared) c.mat = new Material(shared) { name = shared.name + " (body)" };
            return c.mat;
        }

        private void ApplyColors(CharacterCustomizer c)
        {
            var mat = EnsureOwnMaterial(c);
            if (mat == null) return;
            var src = BodyTemplate.SharedMaterial;
            for (int i = 0; i < ColorSlots.Length; i++)
            {
                var slot = ColorSlots[i];
                int v = GetColor(i);
                if (v <= 0 || v > slot.Palette.Length)
                {
                    // As cloned: put back what the NPC wears, so stepping back to 0 is not "keep the last pick".
                    foreach (var p in slot.Props)
                        if (mat.HasProperty(p) && src.HasProperty(p)) mat.SetColor(p, src.GetColor(p));
                    continue;
                }
                slot.Write(mat, slot.Palette[v - 1].Color);
            }
        }

        /// <summary>Destroy a body's private material when the body goes. Safe to call on anything.</summary>
        public static void ReleaseOwnedMaterial(GameObject bodyClone)
        {
            if (bodyClone == null) return;
            var c = bodyClone.GetComponentInChildren<CharacterCustomizer>(true);
            if (c == null || c.mat == null) return;
            // Only a copy this code made. Never the template's, and never a live NPC's.
            if (c.mat != BodyTemplate.SharedMaterial && c.mat.name.EndsWith(" (body)", StringComparison.Ordinal))
            {
                UnityEngine.Object.Destroy(c.mat);
                c.mat = null;
            }
        }

        // ---- persistence / wire ------------------------------------------------------------------------
        // Format: "key=value;key=value". KEY-ADDRESSED on purpose rather than positional, so adding a slot
        // in a later version cannot silently reinterpret an existing player's saved choices as a different
        // body part. Unknown keys are ignored; missing keys keep their default.

        public string Serialize()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Slots.Length; i++)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(Slots[i].Key).Append('=').Append(this[i]);
            }
            for (int i = 0; i < ColorSlots.Length; i++)
                sb.Append(';').Append(ColorSlots[i].Key).Append('=').Append(GetColor(i));
            return sb.ToString();
        }

        public static PlayerAppearance Deserialize(string s)
        {
            var a = Default();
            if (string.IsNullOrEmpty(s)) return a;
            try
            {
                var index = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Slots.Length; i++) index[Slots[i].Key] = i;
                var colorIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < ColorSlots.Length; i++) colorIndex[ColorSlots[i].Key] = i;

                foreach (var pair in s.Split(';'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0 || eq >= pair.Length - 1) continue;
                    var key = pair.Substring(0, eq).Trim();
                    int v;
                    if (!int.TryParse(pair.Substring(eq + 1).Trim(), out v)) continue;
                    int slot;
                    if (colorIndex.TryGetValue(key, out slot)) { a.SetColor(slot, (byte)Mathf.Clamp(v, 0, 255)); continue; }
                    if (!index.TryGetValue(key, out slot)) continue; // slot from a newer build: ignore
                    a[slot] = (byte)Mathf.Clamp(v, 0, 255);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Appearance] Could not parse an appearance string, using the default: " + e.Message);
                return Default();
            }
            return a;
        }

        /// <summary>
        /// A stable, arbitrary-but-varied look derived from a SteamId, used when a player has never opened
        /// the character screen. Better than everyone defaulting to slot 0 and the crew looking like
        /// identical triplets, and deterministic so the same person is recognisably themselves each session.
        /// Deliberately does NOT randomise gender.
        /// </summary>
        public static PlayerAppearance DeterministicFor(ulong steamId)
        {
            var a = Default();
            if (steamId == 0UL) return a;
            // Cheap decorrelation: a different odd multiplier per slot, so neighbouring ids do not produce
            // near-identical characters the way a single hash reused across slots would.
            for (int i = 1; i < Slots.Length; i++)
            {
                ulong h = steamId * (0x9E3779B97F4A7C15UL + ((ulong)i * 2654435761UL));
                h ^= h >> 29;
                a[i] = (byte)(h % 251); // clamped down to the live count at Apply time
            }
            // Skin, hair, cloth and trim get a seeded pick too, so a crew is not all in the port's colors.
            // Leather and metal stay as cloned; they read fine on anyone.
            for (int i = 0; i < 4 && i < ColorSlots.Length; i++)
            {
                ulong h = steamId * (0xD6E8FEB86659FD93UL + ((ulong)(i + 1) * 2246822519UL));
                h ^= h >> 31;
                a.SetColor(i, (byte)(1 + (int)(h % (ulong)ColorSlots[i].Palette.Length)));
            }
            return a;
        }
    }
}
