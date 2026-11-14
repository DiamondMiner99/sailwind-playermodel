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
            /// <summary>The other side of a left and right pair (shoulders, elbows, knees, gloves), or null. Both sides
            /// wear the same choice, each wrapped into its own list.</summary>
            public readonly Func<CharacterCustomizer, List<GameObject>> PairList;
            public readonly Action<CharacterCustomizer, int> PairSet;
            /// <summary>0 is "match the torso" instead of "wear nothing": for limbs, where nothing would leave a stump.</summary>
            public readonly bool MatchesTorso;
            public Slot(string key, string label, Func<CharacterCustomizer, int> get,
                Action<CharacterCustomizer, int> set, Func<CharacterCustomizer, List<GameObject>> list,
                bool allowsNone, Func<CharacterCustomizer, List<GameObject>> pairList = null,
                Action<CharacterCustomizer, int> pairSet = null, bool matchesTorso = false)
            {
                Key = key; Label = label; Get = get; Set = set; List = list; AllowsNone = allowsNone;
                PairList = pairList; PairSet = pairSet; MatchesTorso = matchesTorso;
            }
        }

        // The first nine are the groups that visibly change WHO the character is. The ones after them (head
        // coverings, armor and kit, gloves) were added later and are APPENDED, never inserted: co-op sends the
        // values positionally, so a slot's index is as fixed as its key. The screen shows them in its own order
        // (CharacterScreen.DisplayOrder). Sleeves, elf ears, chest kit and the head attachment folders stay pinned.
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
            // A hood, coif, pelt or helmet: the head coverings made to be worn without hair. Apply hides the hair
            // and the hat while one is on and keeps both choices.
            new Slot("hood", "Hood",
                c => c.allGenderHeadCoverings_No_Hair,
                (c, v) => c.allGenderHeadCoverings_No_Hair = v,
                c => c.allGender != null ? c.allGender.headCoverings_No_Hair : null, allowsNone: true),
            // Coverings made to be worn without facial hair; the beard is hidden under one (FaceCoverHidesBeard).
            new Slot("face", "Face cover",
                c => c.allGenderHeadCoverings_No_FacialHair,
                (c, v) => c.allGenderHeadCoverings_No_FacialHair = v,
                c => c.allGender != null ? c.allGender.headCoverings_No_FacialHair : null, allowsNone: true),
            new Slot("back", "Back",
                c => c.allGenderBack_Attachment,
                (c, v) => c.allGenderBack_Attachment = v,
                c => c.allGender != null ? c.allGender.back_Attachment : null, allowsNone: true),
            new Slot("shoulders", "Shoulders",
                c => c.allGenderShoulder_Attachment_Right,
                (c, v) => c.allGenderShoulder_Attachment_Right = v,
                c => c.allGender != null ? c.allGender.shoulder_Attachment_Right : null, allowsNone: true,
                pairList: c => c.allGender != null ? c.allGender.shoulder_Attachment_Left : null,
                pairSet: (c, v) => c.allGenderShoulder_Attachment_Left = v),
            new Slot("elbows", "Elbows",
                c => c.allGenderElbow_Attachment_Right,
                (c, v) => c.allGenderElbow_Attachment_Right = v,
                c => c.allGender != null ? c.allGender.elbow_Attachment_Right : null, allowsNone: true,
                pairList: c => c.allGender != null ? c.allGender.elbow_Attachment_Left : null,
                pairSet: (c, v) => c.allGenderElbow_Attachment_Left = v),
            new Slot("knees", "Knees",
                c => c.allGenderKnee_Attachement_Right,
                (c, v) => c.allGenderKnee_Attachement_Right = v,
                c => c.allGender != null ? c.allGender.knee_Attachement_Right : null, allowsNone: true,
                pairList: c => c.allGender != null ? c.allGender.knee_Attachement_Left : null,
                pairSet: (c, v) => c.allGenderKnee_Attachement_Left = v),
            new Slot("belt", "Belt",
                c => c.allGenderHips_Attachment,
                (c, v) => c.allGenderHips_Attachment = v,
                c => c.allGender != null ? c.allGender.hips_Attachment : null, allowsNone: true),
            // Hands are a limb: vanilla indexes them unguarded, so there is no "nothing". 0 keeps them matched to
            // the torso, which is how every look saved before this row existed was drawn.
            new Slot("gloves", "Gloves",
                c => c.isFemale ? c.femaleHand_Right : c.maleHand_Right,
                (c, v) => { if (c.isFemale) c.femaleHand_Right = v; else c.maleHand_Right = v; },
                c => Group(c).hand_Right, allowsNone: true,
                pairList: c => Group(c).hand_Left,
                pairSet: (c, v) => { if (c.isFemale) c.femaleHand_Left = v; else c.maleHand_Left = v; },
                matchesTorso: true),
        };

        /// <summary>
        /// How many slots shipped before the head covering, gear and glove rows. A look nobody saved seeds only these,
        /// so the added rows start empty (and the gloves matched to the torso) instead of putting a helmet and armor
        /// on every crewmate who never opened the screen.
        /// </summary>
        public const int CoreSlotCount = 9;


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
            /// <summary>
            /// Turn one chosen color into the shader properties it sets: (chosen, current skin, put). The same table
            /// writes the body's material and a part's property block, so a part painted on its own shades its trim,
            /// leather and metal exactly the way the whole body would.
            /// </summary>
            public readonly Action<Color, Color, Action<string, Color>> Expand;
            /// <summary>Offered on a single part (cloth, trim, leather, metal). Skin and hair belong to the whole body.</summary>
            public readonly bool PerPart;
            public ColorSlot(string key, string label, string[] props, ColorOption[] palette,
                Action<Color, Color, Action<string, Color>> expand, bool perPart)
            { Key = key; Label = label; Props = props; Palette = palette; Expand = expand; PerPart = perPart; }
        }

        static readonly ColorOption[] ClothPalette =
        {
            new ColorOption("Linen", 0.92f, 0.90f, 0.85f), new ColorOption("Cream", 0.90f, 0.84f, 0.68f),
            new ColorOption("Sand", 0.80f, 0.70f, 0.52f),  new ColorOption("Tan", 0.66f, 0.52f, 0.36f),
            new ColorOption("Umber", 0.45f, 0.32f, 0.22f), new ColorOption("Coffee", 0.28f, 0.20f, 0.14f),
            new ColorOption("Brick", 0.70f, 0.18f, 0.16f),   new ColorOption("Maroon", 0.45f, 0.12f, 0.14f),
            new ColorOption("Pumpkin", 0.85f, 0.45f, 0.15f), new ColorOption("Mustard", 0.80f, 0.65f, 0.20f),
            new ColorOption("Forest", 0.25f, 0.50f, 0.25f), new ColorOption("Olive", 0.40f, 0.42f, 0.22f),
            new ColorOption("Teal", 0.18f, 0.48f, 0.48f),  new ColorOption("Sky", 0.49f, 0.70f, 0.83f),
            new ColorOption("Denim", 0.25f, 0.42f, 0.70f),  new ColorOption("Navy", 0.14f, 0.20f, 0.36f),
            new ColorOption("Plum", 0.42f, 0.25f, 0.50f), new ColorOption("Ash", 0.55f, 0.55f, 0.55f),
            new ColorOption("Charcoal", 0.25f, 0.25f, 0.27f), new ColorOption("Black", 0.10f, 0.10f, 0.11f),
        };
        static readonly ColorOption[] LeatherPalette =
        {
            new ColorOption("Tan", 0.62f, 0.45f, 0.30f),   new ColorOption("Saddle", 0.42f, 0.29f, 0.20f),
            new ColorOption("Chocolate", 0.28f, 0.19f, 0.13f), new ColorOption("Oxblood", 0.48f, 0.22f, 0.16f),
            new ColorOption("Black", 0.12f, 0.11f, 0.10f), new ColorOption("Smoke", 0.40f, 0.38f, 0.36f),
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
                (c, skin, put) => { put("_Color_Skin", c); put("_Color_Scar", new Color(c.r * 0.95f, c.g * 0.72f, c.b * 0.66f)); },
                perPart: false),
            new ColorSlot("c_hair", "Hair color", new[] { "_Color_Hair", "_Color_Stubble" }, HairPalette,
                (c, skin, put) => { put("_Color_Hair", c); put("_Color_Stubble", Color.Lerp(c, skin, 0.5f)); },
                perPart: false),
            new ColorSlot("c_primary", "Cloth", new[] { "_Color_Primary" }, ClothPalette,
                (c, skin, put) => put("_Color_Primary", c), perPart: true),
            new ColorSlot("c_secondary", "Trim", new[] { "_Color_Secondary" }, ClothPalette,
                (c, skin, put) => put("_Color_Secondary", c), perPart: true),
            new ColorSlot("c_leather", "Leather", new[] { "_Color_Leather_Primary", "_Color_Leather_Secondary" }, LeatherPalette,
                (c, skin, put) => { put("_Color_Leather_Primary", c); put("_Color_Leather_Secondary", Scale(c, 1.2f)); },
                perPart: true),
            new ColorSlot("c_metal", "Metal", new[] { "_Color_Metal_Primary", "_Color_Metal_Secondary", "_Color_Metal_Dark" }, MetalPalette,
                (c, skin, put) => { put("_Color_Metal_Primary", c); put("_Color_Metal_Secondary", Scale(c, 0.8f)); put("_Color_Metal_Dark", Scale(c, 0.45f)); },
                perPart: true),
        };

        /// <summary>
        /// Rows that can be painted on their own: the clothing and gear. Head, hair, eyebrows and facial hair carry
        /// skin and hair colors, which belong to the whole body, and gender is not a part.
        /// </summary>
        public static bool IsPaintable(int slot)
        {
            if (slot < 0 || slot >= Slots.Length) return false;
            switch (Slots[slot].Key)
            {
                case "gender": case "head": case "hair": case "eyebrow": case "facialhair": return false;
                default: return true;
            }
        }

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
            return NearestPaletteIndex(colorSlot, m.GetColor(slot.Props[0]));
        }

        /// <summary>The palette entry closest to a color, 1-based; 0 for an unknown slot.</summary>
        public static int NearestPaletteIndex(int colorSlot, Color c)
        {
            if (colorSlot < 0 || colorSlot >= ColorSlots.Length) return 0;
            var slot = ColorSlots[colorSlot];
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

        // ---- stubble -------------------------------------------------------------------------------------
        // The shadow of a shave on a male head is a region of the shared atlas, tinted by _Color_Stubble. 0 keeps the
        // shade the hair color gives it (every look saved before this existed), 1 is a light shadow, 2 none: the
        // region takes the skin color. Saved as "stubble=N", only when not 0, so older builds skip it.

        /// <summary>0 full, 1 light, 2 none.</summary>
        public byte Stubble;

        public static readonly string[] StubbleNames = { "Full", "Light", "None" };
        private const string StubbleKey = "stubble";

        /// <summary>Lighten or remove the stubble the hair color (or the cloned NPC) just set. Runs after the hair.</summary>
        private void ApplyStubble(Material mat)
        {
            if (Stubble == 0 || !mat.HasProperty("_Color_Stubble") || !mat.HasProperty("_Color_Skin")) return;
            Color skin = mat.GetColor("_Color_Skin");
            Color full = mat.GetColor("_Color_Stubble");
            SetColor(mat, "_Color_Stubble", Stubble >= 2 ? skin : Color.Lerp(full, skin, 0.6f));
        }

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

        // ---- custom colors -------------------------------------------------------------------------------
        // Any color, not only the palette's, for the whole body (Rgb) and for one part (PartRgb). Alpha 0 is "not set".
        // A whole-body custom color keeps the nearest palette entry in Colors too, so a build or a crewmate that only
        // knows the palette still shows something close. Saved as extra keys that older builds skip.

        /// <summary>Per color slot, a custom color for the whole body. Alpha 0 = use Colors. Null/short = none set.</summary>
        public Color32[] Rgb;

        /// <summary>Per part and color slot, [slot * ColorSlotCount + colorSlot]. Alpha 0 = the body's color.</summary>
        public Color32[] PartRgb;

        public bool TryGetRgb(int colorSlot, out Color32 c)
        {
            c = default(Color32);
            if (Rgb == null || colorSlot < 0 || colorSlot >= Rgb.Length || Rgb[colorSlot].a == 0) return false;
            c = Rgb[colorSlot];
            return true;
        }

        /// <summary>Set (or with null, clear) a whole-body custom color. Setting one also points Colors at its nearest entry.</summary>
        public void SetRgb(int colorSlot, Color32? c)
        {
            if (colorSlot < 0 || colorSlot >= ColorSlots.Length) return;
            SetRgbRaw(colorSlot, c);
            if (c.HasValue) SetColor(colorSlot, (byte)NearestPaletteIndex(colorSlot, (Color)c.Value));
        }

        private void SetRgbRaw(int colorSlot, Color32? c)
        {
            if (colorSlot < 0 || colorSlot >= ColorSlots.Length) return;
            if (Rgb == null || Rgb.Length < ColorSlots.Length)
            {
                if (!c.HasValue) return;
                var next = new Color32[ColorSlots.Length];
                if (Rgb != null) Array.Copy(Rgb, next, Math.Min(Rgb.Length, next.Length));
                Rgb = next;
            }
            Rgb[colorSlot] = c.HasValue ? Opaque(c.Value) : default(Color32);
        }

        public bool TryGetPartRgb(int slot, int colorSlot, out Color32 c)
        {
            c = default(Color32);
            int k = PartIndex(slot, colorSlot);
            if (k < 0 || PartRgb == null || k >= PartRgb.Length || PartRgb[k].a == 0) return false;
            c = PartRgb[k];
            return true;
        }

        /// <summary>Paint one part's color slot (null goes back to the body's color). Only paintable parts and per-part slots.</summary>
        public void SetPartRgb(int slot, int colorSlot, Color32? c)
        {
            int k = PartIndex(slot, colorSlot);
            if (k < 0 || !IsPaintable(slot) || !ColorSlots[colorSlot].PerPart) return;
            int size = Slots.Length * ColorSlots.Length;
            if (PartRgb == null || PartRgb.Length < size)
            {
                if (!c.HasValue) return;
                var next = new Color32[size];
                if (PartRgb != null) Array.Copy(PartRgb, next, Math.Min(PartRgb.Length, next.Length));
                PartRgb = next;
            }
            PartRgb[k] = c.HasValue ? Opaque(c.Value) : default(Color32);
        }

        /// <summary>True if any of this part's colors is painted on its own.</summary>
        public bool HasPartColors(int slot)
        {
            for (int i = 0; i < ColorSlots.Length; i++)
            {
                Color32 c;
                if (TryGetPartRgb(slot, i, out c)) return true;
            }
            return false;
        }

        /// <summary>Drop every custom color, whole-body and per-part. The palette picks stay.</summary>
        public void ClearCustomColors()
        {
            Rgb = null;
            PartRgb = null;
            PatBody = null;
            PatPart = null;
        }

        /// <summary>
        /// The color a whole-body slot shows: the custom color, else the palette entry, else what the cloned NPC wears.
        /// False only when none of those can be known yet (no template).
        /// </summary>
        public bool TryGetBodyColor(int colorSlot, out Color c)
        {
            c = Color.white;
            if (colorSlot < 0 || colorSlot >= ColorSlots.Length) return false;
            Color32 custom;
            if (TryGetRgb(colorSlot, out custom)) { c = custom; return true; }
            int v = GetColor(colorSlot);
            var p = ColorSlots[colorSlot].Palette;
            if (v > 0 && v <= p.Length) { c = p[v - 1].Color; return true; }
            var m = BodyTemplate.SharedMaterial;
            var props = ColorSlots[colorSlot].Props;
            if (m == null || props.Length == 0 || !m.HasProperty(props[0])) return false;
            c = m.GetColor(props[0]);
            c.a = 1f;
            return true;
        }

        private static int PartIndex(int slot, int colorSlot)
        {
            if (slot < 0 || slot >= Slots.Length || colorSlot < 0 || colorSlot >= ColorSlots.Length) return -1;
            return slot * ColorSlots.Length + colorSlot;
        }

        // ---- patterns ----------------------------------------------------------------------------------------
        // A pattern over the cloth or trim color: stripes, pinstripes, checks, plaid or a fade, in a second color and one
        // a size from small to large. Drawn by the PatternedBody shader (see BodyPattern.cginc), which a body switches to only while
        // its look has one. Like the custom colors, whole-body (PatBody) or for one part (PatPart), saved as extra keys
        // ("c_primary_pat=stripes,2,1A2B3C", "torso.c_primary_pat=none,2,000000") that older builds skip.
        //
        // A part's entry, when there is one, replaces the body's, so a part can also be set to None under a striped body.

        /// <summary>One pattern choice. Set is false for "no entry": the body's pattern for a part, none for the body.</summary>
        public struct Pattern
        {
            public bool Set;
            public byte Type;     // index into PatternKeys; 0 is none. The shader's type numbers are the same.
            public byte Size;     // 0 small to 255 large; the shader gets Size / 255
            public Color32 Color;
        }

        /// <summary>Saved tokens, in the shader's type order. NEVER reordered or renamed once shipped.</summary>
        public static readonly string[] PatternKeys =
        {
            "none", "stripes", "pinstripes", "checks", "plaid", "fade",
            "breton", "diagonal", "chevron", "diamonds", "argyle", "gingham", "windowpane", "waves", "dots", "halves", "quartered",
        };
        public static readonly string[] PatternNames =
        {
            "None", "Stripes", "Pinstripes", "Checks", "Plaid", "Fade",
            "Breton", "Diagonal", "Chevron", "Diamonds", "Argyle", "Gingham", "Windowpane", "Waves", "Dots", "Halves", "Quartered",
        };

        /// <summary>Pattern entries, per color slot for the whole body. Null/short = none.</summary>
        public Pattern[] PatBody;

        /// <summary>Pattern entries per part and color slot, [slot * ColorSlotCount + colorSlot]. Null/short = none.</summary>
        public Pattern[] PatPart;

        /// <summary>Cloth and trim take patterns. The shader has room for exactly these two.</summary>
        public static bool TakesPattern(int colorSlot)
        {
            if (colorSlot < 0 || colorSlot >= ColorSlots.Length) return false;
            var k = ColorSlots[colorSlot].Key;
            return k == "c_primary" || k == "c_secondary";
        }

        public bool TryGetBodyPattern(int colorSlot, out Pattern p)
        {
            p = default(Pattern);
            if (PatBody == null || colorSlot < 0 || colorSlot >= PatBody.Length || !PatBody[colorSlot].Set) return false;
            p = PatBody[colorSlot];
            return true;
        }

        public void SetBodyPattern(int colorSlot, Pattern? p)
        {
            if (!TakesPattern(colorSlot)) return;
            if (PatBody == null || PatBody.Length < ColorSlots.Length)
            {
                if (!p.HasValue) return;
                var next = new Pattern[ColorSlots.Length];
                if (PatBody != null) Array.Copy(PatBody, next, Math.Min(PatBody.Length, next.Length));
                PatBody = next;
            }
            PatBody[colorSlot] = p.HasValue ? Clean(p.Value) : default(Pattern);
        }

        public bool TryGetPartPattern(int slot, int colorSlot, out Pattern p)
        {
            p = default(Pattern);
            int k = PartIndex(slot, colorSlot);
            if (k < 0 || PatPart == null || k >= PatPart.Length || !PatPart[k].Set) return false;
            p = PatPart[k];
            return true;
        }

        public void SetPartPattern(int slot, int colorSlot, Pattern? p)
        {
            int k = PartIndex(slot, colorSlot);
            if (k < 0 || !IsPaintable(slot) || !TakesPattern(colorSlot)) return;
            int size = Slots.Length * ColorSlots.Length;
            if (PatPart == null || PatPart.Length < size)
            {
                if (!p.HasValue) return;
                var next = new Pattern[size];
                if (PatPart != null) Array.Copy(PatPart, next, Math.Min(PatPart.Length, next.Length));
                PatPart = next;
            }
            PatPart[k] = p.HasValue ? Clean(p.Value) : default(Pattern);
        }

        /// <summary>The pattern a part shows in a color slot: its own entry, else the body's. Type 0 for none.</summary>
        public Pattern EffectivePattern(int slot, int colorSlot)
        {
            Pattern p;
            if (TryGetPartPattern(slot, colorSlot, out p)) return p;
            if (TryGetBodyPattern(colorSlot, out p)) return p;
            return default(Pattern);
        }

        /// <summary>True if this part has a pattern entry of its own (None included).</summary>
        public bool HasPartPatterns(int slot)
        {
            for (int i = 0; i < ColorSlots.Length; i++)
            {
                Pattern p;
                if (TryGetPartPattern(slot, i, out p)) return true;
            }
            return false;
        }

        /// <summary>True if any part could show a pattern, which is when a body needs the patterned shader.</summary>
        public bool HasAnyPattern()
        {
            if (PatBody != null) foreach (var p in PatBody) if (p.Set && p.Type > 0) return true;
            if (PatPart != null) foreach (var p in PatPart) if (p.Set && p.Type > 0) return true;
            return false;
        }

        private static Pattern Clean(Pattern p)
        {
            return new Pattern
            {
                Set = true,
                Type = p.Type < PatternKeys.Length ? p.Type : (byte)0,
                Size = p.Size,
                Color = Opaque(p.Color),
            };
        }

        private static string FormatPattern(Pattern p)
        {
            return PatternKeys[p.Type < PatternKeys.Length ? p.Type : 0] + "," + p.Size + "," + ToHex(p.Color);
        }

        private static bool TryParsePattern(string s, out Pattern p)
        {
            p = default(Pattern);
            var parts = s.Split(',');
            if (parts.Length != 3) return false;
            int type = Array.IndexOf(PatternKeys, parts[0].Trim());
            int size;
            Color32 c;
            if (type < 0 || !int.TryParse(parts[1].Trim(), out size) || !TryParseHex(parts[2], out c)) return false;
            p = Clean(new Pattern { Type = (byte)type, Size = (byte)Mathf.Clamp(size, 0, 255), Color = c });
            return true;
        }

        /// <summary>
        /// Put a pattern's type, size and color into a material or a property block, as the shader's _PatA (cloth) or
        /// _PatB (trim).
        /// </summary>
        private static void PutPattern(int colorSlot, Pattern p, Action<string, Vector4> vec, Action<string, Color> col)
        {
            string name = ColorSlots[colorSlot].Key == "c_primary" ? "_PatA" : "_PatB";
            vec(name, new Vector4(p.Set ? p.Type : 0, p.Size / 255f, 0, 0));
            col(name + "Color", (Color)p.Color);
        }

        private static Color32 Opaque(Color32 c) { return new Color32(c.r, c.g, c.b, 255); }

        public static string ToHex(Color32 c) { return c.r.ToString("X2") + c.g.ToString("X2") + c.b.ToString("X2"); }

        public static bool TryParseHex(string s, out Color32 c)
        {
            c = default(Color32);
            if (s == null) return false;
            s = s.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length != 6) return false;
            int v;
            if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
            c = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
            return true;
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
        private void PinUnexposedGroups(CharacterCustomizer c, bool rightHandPicked, bool leftHandPicked)
        {
            var g = Group(c);

            // Accessories no row offers: nothing. The head coverings, back, shoulders, elbows, knees and belt
            // have rows of their own now and were written by Apply.
            c.allGenderAll_Head_Attachment = -1;          // the folder group - never valid to select
            c.allGenderChest_Attachment = -1;             // empty on every NPC this mod clones
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
            // Gloves picked on their own row were written by Apply; a hand it did not write goes with the torso.
            if (!rightHandPicked) SetLimb(c, g.hand_Right, torso, v => { if (c.isFemale) c.femaleHand_Right = v; else c.maleHand_Right = v; });
            if (!leftHandPicked)  SetLimb(c, g.hand_Left,  torso, v => { if (c.isFemale) c.femaleHand_Left  = v; else c.maleHand_Left  = v; });
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
            return NthWearable(Slots[slotIndex].List(c), value);
        }

        /// <summary>
        /// The list index of the nth wearable entry (1-based), wrapped into how many the list holds; -1 for 0 or a list
        /// with nothing wearable. Wrapping is what lets the two sides of a pair, whose lists need not be the same
        /// length, wear one choice.
        /// </summary>
        private static int NthWearable(List<GameObject> list, int value)
        {
            if (value <= 0 || list == null) return -1;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (IsWearable(list[i])) n++;
            if (n == 0) return -1;
            int wanted = (value - 1) % n + 1;
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

                bool rightHand = false, leftHand = false;   // gloves written here rather than matched to the torso
                for (int i = 1; i < Slots.Length; i++)
                {
                    int count = VariantCount(c, i);
                    // Nothing wearable in this group. Only write -1 where vanilla GUARDS it: UpdateModel
                    // indexes the BODY groups unguarded (male.eyebrow[maleEyebrow] and friends), so -1 on
                    // one of those throws mid-build and every part after it never activates. Leaving the
                    // source value alone is strictly safer than writing one that aborts the model.
                    if (count <= 0)
                    {
                        // A limb row has no "nothing"; the torso match in PinUnexposedGroups covers it.
                        if (Slots[i].AllowsNone && !Slots[i].MatchesTorso)
                        {
                            Slots[i].Set(c, -1);
                            if (Slots[i].PairSet != null) Slots[i].PairSet(c, -1);
                        }
                        continue;
                    }

                    // Value space is 0 = nothing, 1..count = the nth wearable entry. WRAP rather than
                    // clamp-to-zero: the deterministic per-SteamId look draws from a wide range and most
                    // groups hold only a handful of variants, so clamping collapsed the majority of slots
                    // to one value and made crews look near-identical - exactly what that look exists to
                    // prevent. Wrapping keeps the variation and is equally safe.
                    int v = NormalizeValue(i, count, this[i]);
                    if (Slots[i].MatchesTorso && v == 0) continue;   // PinUnexposedGroups matches it to the torso
                    int main = ToVanillaIndex(c, i, v);
                    // A limb is never given -1: that side is left to the torso match instead.
                    if (main >= 0 || !Slots[i].MatchesTorso) Slots[i].Set(c, main);
                    if (Slots[i].MatchesTorso && main >= 0) rightHand = true;
                    if (Slots[i].PairSet != null)
                    {
                        // A pair side with nothing wearable goes to -1 where vanilla guards it, and to the torso
                        // match on a limb.
                        int pair = NthWearable(Slots[i].PairList(c), v);
                        if (pair >= 0 || !Slots[i].MatchesTorso) Slots[i].PairSet(c, pair);
                        if (Slots[i].MatchesTorso && pair >= 0) leftHand = true;
                    }
                }

                // A hood is made to be worn without hair, so the hair and the hat come off under it. The saved
                // choices stay; taking the hood off brings them back.
                if (c.allGenderHeadCoverings_No_Hair >= 0)
                {
                    c.allGenderAll_Hair = -1;
                    c.allGenderHeadCoverings_Base_Hair = -1;
                }
                // A face cover is made to be worn without facial hair. Vanilla draws the beard unguarded, so it
                // cannot be switched off by index; FaceCoverHidesBeard hides it after the model is built.
                SetHidesBeard(c, !c.isFemale && c.allGenderHeadCoverings_No_FacialHair >= 0);

                // Pin every group we do NOT expose to "nothing". Without this they keep whatever the local
                // machine's cloned shopkeeper happened to be wearing - so the same appearance string would
                // render differently on every client, which defeats the point of syncing it at all.
                PinUnexposedGroups(c, rightHand, leftHand);

                // Last: make every index legal for THIS rig. Vanilla's UpdateModel indexes the body groups
                // unguarded, and the moment one throws, every part after it never activates and the body is
                // left at Start's bare defaults. A rig with a different part library (another NPC prefab, a
                // game update) must never be able to do that.
                SanitizeIndices(c);

                ApplyColors(c);
                ApplyPartColors(c);
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
            Action<string, Color> put = (p, col) => SetColor(mat, p, col);
            for (int i = 0; i < ColorSlots.Length; i++)
            {
                var slot = ColorSlots[i];
                Color32 custom;
                int v = GetColor(i);
                Color chosen;
                if (TryGetRgb(i, out custom)) chosen = custom;
                else if (v > 0 && v <= slot.Palette.Length) chosen = slot.Palette[v - 1].Color;
                else
                {
                    // As cloned: put back what the NPC wears, so stepping back to 0 is not "keep the last pick".
                    foreach (var p in slot.Props)
                        if (mat.HasProperty(p) && src.HasProperty(p)) mat.SetColor(p, src.GetColor(p));
                    continue;
                }
                // Skin is written before hair, so the stubble blend reads the skin just set.
                slot.Expand(chosen, SkinOf(mat, chosen), put);
            }
            ApplyStubble(mat);
            ApplyPatternShader(c, mat, src);
        }

        /// <summary>
        /// Switch the body's material to the patterned shader while the look has a pattern, and back to the game's own
        /// when it has none, so a look without patterns renders exactly as before. The property names match, so the
        /// colors carry across the swap. The body-wide patterns go on the material; parts with their own are handled by
        /// ApplyPartColors, and BodyPatterns keeps each part's pattern placed on its bone every frame.
        /// </summary>
        private void ApplyPatternShader(CharacterCustomizer c, Material mat, Material src)
        {
            bool want = HasAnyPattern();
            Shader patterned = want ? BodyShaders.Patterned : null;   // only loads the bundle when a pattern is wanted
            if (want && patterned != null)
            {
                if (mat.shader != patterned) mat.shader = patterned;
                for (int i = 0; i < ColorSlots.Length; i++)
                {
                    if (!TakesPattern(i)) continue;
                    Pattern p;
                    TryGetBodyPattern(i, out p);   // default (none) when there is no entry
                    PutPattern(i, p, (n, v) => mat.SetVector(n, v), (n, col) => mat.SetColor(n, col));
                }
                BodyPatterns.Track(c);
            }
            else
            {
                if (src != null && mat.shader != src.shader) mat.shader = src.shader;
                BodyPatterns.Untrack(c);
            }
        }

        private static Color SkinOf(Material m, Color fallback)
        {
            return m != null && m.HasProperty("_Color_Skin") ? m.GetColor("_Color_Skin") : fallback;
        }

        private static MaterialPropertyBlock _block;

        /// <summary>
        /// Paint parts on their own. Every part shares the body's one material, so a part's own colors go into a
        /// MaterialPropertyBlock on its renderers instead: no material copies, and the seated first-person fade, which
        /// swaps the material, keeps them because the block belongs to the renderer and the fade shader uses the same
        /// property names. A renderer of a part with nothing painted has its block cleared, so a part put back to the
        /// body's color really goes back. Nothing else in this mod uses property blocks on the body.
        ///
        /// The renderers are found through the part lists, every variant of the part and not only the one worn, so a
        /// variant switched to later is already painted. The arms are part of the torso (they follow it). The hands are
        /// the gloves': when gloves are picked, or when the Gloves row is painted while it matches the torso, since then
        /// the hands are what that row's colors can only mean. Otherwise they are the torso's.
        /// </summary>
        private void ApplyPartColors(CharacterCustomizer c)
        {
            var mat = c.mat != null ? c.mat : BodyTemplate.SharedMaterial;
            if (mat == null) return;
            if (_block == null) _block = new MaterialPropertyBlock();
            var g = Group(c);
            int gloves = SlotIndex("gloves");
            bool glovesOwnHands = gloves >= 0 && (this[gloves] != 0 || HasPartColors(gloves) || HasPartPatterns(gloves));
            Color skin = SkinOf(mat, Color.white);
            // HasAnyPattern first, so a look without patterns never loads the shader bundle.
            bool patterned = HasAnyPattern() && BodyShaders.Patterned != null && mat.shader == BodyShaders.Patterned;

            for (int s = 0; s < Slots.Length; s++)
            {
                if (!IsPaintable(s)) continue;
                if (s == gloves && !glovesOwnHands) continue;   // the hands are the torso's then

                var lists = new List<List<GameObject>>(6);
                lists.Add(Slots[s].List(c));
                if (Slots[s].PairList != null) lists.Add(Slots[s].PairList(c));
                if (Slots[s].Key == "torso")
                {
                    lists.Add(g.arm_Upper_Right); lists.Add(g.arm_Upper_Left);
                    lists.Add(g.arm_Lower_Right); lists.Add(g.arm_Lower_Left);
                    if (!glovesOwnHands) { lists.Add(g.hand_Right); lists.Add(g.hand_Left); }
                }

                bool any = false;
                _block.Clear();
                for (int k = 0; k < ColorSlots.Length; k++)
                {
                    Color32 col;
                    if (!ColorSlots[k].PerPart || !TryGetPartRgb(s, k, out col)) continue;
                    any = true;
                    ColorSlots[k].Expand(col, skin, (p, v) =>
                    {
                        if (!mat.HasProperty(p)) return;
                        _block.SetColor(p, new Color(v.r, v.g, v.b, mat.GetColor(p).a));
                    });
                }
                // The part's own patterns. Only while the patterned shader is on; otherwise nothing reads them.
                if (patterned)
                    for (int k = 0; k < ColorSlots.Length; k++)
                    {
                        Pattern pat;
                        if (!TakesPattern(k) || !TryGetPartPattern(s, k, out pat)) continue;
                        any = true;
                        PutPattern(k, pat, (n, v) => _block.SetVector(n, v), (n, v) => _block.SetColor(n, v));
                    }

                foreach (var list in lists)
                {
                    if (list == null) continue;
                    foreach (var go in list)
                    {
                        if (go == null) continue;
                        var r = go.GetComponent<SkinnedMeshRenderer>();
                        if (r == null) continue;
                        if (any) r.SetPropertyBlock(_block);
                        else if (r.HasPropertyBlock()) r.SetPropertyBlock(null);
                    }
                }
            }
        }

        private static int SlotIndex(string key)
        {
            for (int i = 0; i < Slots.Length; i++) if (Slots[i].Key == key) return i;
            return -1;
        }

        /// <summary>
        /// Recolor a body that is already built, without rebuilding the model: for a color being dragged in the picker,
        /// where a full restyle per frame would be wasted work. The parts worn must not have changed since the last
        /// Apply. A seated first-person fade keeps its copy of the body's colors until the next full restyle; the
        /// per-part colors reach it anyway. Never throws.
        /// </summary>
        public void ApplyColorsLive(CharacterCustomizer c)
        {
            if (c == null) return;
            try
            {
                ApplyColors(c);
                ApplyPartColors(c);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Appearance] Could not recolor: " + e.Message);
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

        // ---- face covers ---------------------------------------------------------------------------------
        // Bodies this mod dressed with a face cover, by customizer instance id. Only ours: the port's NPCs share the
        // class and are never touched.
        private static readonly HashSet<int> _hidesBeard = new HashSet<int>();

        private static void SetHidesBeard(CharacterCustomizer c, bool hide)
        {
            if (hide) _hidesBeard.Add(c.GetInstanceID());
            else _hidesBeard.Remove(c.GetInstanceID());
        }

        /// <summary>Called when a body goes, so the table does not keep dead ids. Safe to call on anything.</summary>
        public static void Forget(GameObject bodyClone)
        {
            if (bodyClone == null) return;
            var c = bodyClone.GetComponentInChildren<CharacterCustomizer>(true);
            if (c != null) _hidesBeard.Remove(c.GetInstanceID());
            BodyPatterns.Untrack(c);
        }

        /// <summary>
        /// Postfix on CharacterCustomizer.UpdateModel (private, so registered through GuardedPatches): on a body this
        /// mod dressed with a face cover, the beard UpdateModel just switched on goes off again. ClearItems switches it
        /// off on the next build anyway, so leaving it in the customizer's own list is harmless. A skipped patch only
        /// lets the beard show through the cover.
        /// </summary>
        internal static class FaceCoverHidesBeard
        {
            internal static void Postfix(CharacterCustomizer __instance)
            {
                try
                {
                    if (__instance == null || __instance.isFemale || !_hidesBeard.Contains(__instance.GetInstanceID())) return;
                    var beards = __instance.male != null ? __instance.male.facialHair : null;
                    int i = __instance.maleFacialHair;
                    if (beards != null && i >= 0 && i < beards.Count && beards[i] != null) beards[i].SetActive(false);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[Appearance] Could not hide the beard under the face cover: " + e.Message);
                }
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
            if (Stubble != 0) sb.Append(';').Append(StubbleKey).Append('=').Append(Stubble);
            // Custom colors as extra keys: "c_primary_rgb=A1B2C3" for the whole body, "torso.c_primary=A1B2C3" for
            // one part. A build that does not know them skips them and keeps the palette pick written above.
            for (int i = 0; i < ColorSlots.Length; i++)
            {
                Color32 c;
                if (TryGetRgb(i, out c)) sb.Append(';').Append(ColorSlots[i].Key).Append(RgbSuffix).Append('=').Append(ToHex(c));
            }
            for (int s = 0; s < Slots.Length; s++)
                for (int i = 0; i < ColorSlots.Length; i++)
                {
                    Color32 c;
                    if (TryGetPartRgb(s, i, out c))
                        sb.Append(';').Append(Slots[s].Key).Append('.').Append(ColorSlots[i].Key).Append('=').Append(ToHex(c));
                }
            // Patterns, the same way: "c_primary_pat=stripes,2,1A2B3C" and "torso.c_primary_pat=none,2,000000".
            for (int i = 0; i < ColorSlots.Length; i++)
            {
                Pattern p;
                if (TryGetBodyPattern(i, out p)) sb.Append(';').Append(ColorSlots[i].Key).Append(PatSuffix).Append('=').Append(FormatPattern(p));
            }
            for (int s = 0; s < Slots.Length; s++)
                for (int i = 0; i < ColorSlots.Length; i++)
                {
                    Pattern p;
                    if (TryGetPartPattern(s, i, out p))
                        sb.Append(';').Append(Slots[s].Key).Append('.').Append(ColorSlots[i].Key).Append(PatSuffix).Append('=').Append(FormatPattern(p));
                }
            return sb.ToString();
        }

        private const string RgbSuffix = "_rgb", PatSuffix = "_pat";

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
                    var value = pair.Substring(eq + 1).Trim();
                    int slot;
                    Color32 rgb;
                    Pattern pat;
                    // "torso.c_primary=A1B2C3" and "torso.c_primary_pat=...": one part on its own. The setters drop
                    // anything not paintable or not patternable.
                    int dot = key.IndexOf('.');
                    if (dot > 0)
                    {
                        int part;
                        if (!index.TryGetValue(key.Substring(0, dot), out part)) continue;
                        string colorKey = key.Substring(dot + 1);
                        if (colorKey.EndsWith(PatSuffix, StringComparison.Ordinal))
                        {
                            if (colorIndex.TryGetValue(colorKey.Substring(0, colorKey.Length - PatSuffix.Length), out slot) && TryParsePattern(value, out pat))
                                a.SetPartPattern(part, slot, pat);
                        }
                        else if (colorIndex.TryGetValue(colorKey, out slot) && TryParseHex(value, out rgb))
                            a.SetPartRgb(part, slot, rgb);
                        continue;
                    }
                    // "c_primary_rgb=A1B2C3": a whole-body custom color. The palette key beside it is left as written.
                    if (key.EndsWith(RgbSuffix, StringComparison.Ordinal))
                    {
                        if (colorIndex.TryGetValue(key.Substring(0, key.Length - RgbSuffix.Length), out slot) && TryParseHex(value, out rgb))
                            a.SetRgbRaw(slot, rgb);
                        continue;
                    }
                    // "c_primary_pat=stripes,2,1A2B3C": a whole-body pattern.
                    if (key.EndsWith(PatSuffix, StringComparison.Ordinal))
                    {
                        if (colorIndex.TryGetValue(key.Substring(0, key.Length - PatSuffix.Length), out slot) && TryParsePattern(value, out pat))
                            a.SetBodyPattern(slot, pat);
                        continue;
                    }
                    int v;
                    if (!int.TryParse(value, out v)) continue;
                    if (key == StubbleKey) { a.Stubble = (byte)Mathf.Clamp(v, 0, StubbleNames.Length - 1); continue; }
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
            // near-identical characters the way a single hash reused across slots would. Only the core slots: the
            // rows added after them stay at 0 (no hood, no gear, gloves with the torso).
            for (int i = 1; i < CoreSlotCount && i < Slots.Length; i++)
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
