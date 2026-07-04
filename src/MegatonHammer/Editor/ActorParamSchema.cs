namespace MegatonHammer.Editor;

/// <summary>
/// Typed decode of an actor's packed 16-bit <c>params</c> ("variable") into named bit-fields,
/// the OoT/MM analogue of Valve Hammer's typed entity keyvalues. Where Hammer exposes an entity's
/// logic as named choices/flags/integers, OoT packs that same logic into <c>PARAMS_GET_U(params,
/// shift, len)</c> slices — actor type, subtype, switch/collectible flags, drop contents, etc. This
/// registry turns those slices back into labelled dropdowns, spinners and checkboxes in the entity
/// property sheet. Layouts are taken verbatim from the OoT decomp actor sources.
/// </summary>
public static class ActorParamSchema
{
    public enum FieldKind { Enum, Int, Flag, Message }

    /// <summary>Which game-flag namespace a field indexes (used by the flag-connections view), or None.
    /// Clear = room-clear (kill-all-enemies; index is the room number, set by a Note not a param field);
    /// Event = OoT eventChkInf / MM weekEventReg story flags; GoldSkulltula = the GS-token flag space.</summary>
    public enum FlagKind { None, Switch, Chest, Collectible, Scene, Clear, Event, GoldSkulltula }

    /// <summary>Whether the actor sets the flag, reads/responds to it, or both — the Z64 analogue of
    /// Hammer's entity output (Setter) vs input (Reader).</summary>
    public enum FlagRole { None, Setter, Reader, Both }

    /// <summary>One bit-field slice of <c>params</c>: bits [Shift .. Shift+Length-1]. For an Enum whose game
    /// values are SIGNED (e.g. Tektite type -2/-1, ReDead -3..3), set <c>EnumBase</c> to the value that
    /// Options[0] represents (the most-negative one); the field then decodes via two's-complement.</summary>
    public sealed record Field(string Name, int Shift, int Length, FieldKind Kind,
                               string? Desc = null, IReadOnlyList<string>? Options = null,
                               FlagKind Flag = FlagKind.None, FlagRole Role = FlagRole.None,
                               int TextIdBase = 0, bool FromRotZ = false, bool Advanced = false,
                               int EnumBase = 0)
    {
        /// <summary>Hidden from the basic (default) actor properties, shown only under "Show Advanced Options".
        /// Any logic-FLAG field is advanced (switch/treasure/collectible/GS wiring — the editor auto-manages the
        /// self-state ones and casual users don't hand-tune them); a field can also opt in via <c>Advanced</c>
        /// (transition indices, raw sub-selectors). Type/contents/size/appearance fields stay BASIC.</summary>
        public bool IsAdvanced => Advanced || Flag != FlagKind.None;

        public int Mask => (1 << Length) - 1;
        public int Get(ushort vars) => (vars >> Shift) & Mask;
        public ushort Set(ushort vars, int value) =>
            (ushort)((vars & ~(Mask << Shift)) | ((value & Mask) << Shift));

        /// <summary>The field's game value: sign-extended over Length for a signed Enum (EnumBase &lt; 0), else raw.</summary>
        public int SignedGet(ushort vars)
        {
            int raw = Get(vars);
            return (EnumBase < 0 && (raw & (1 << (Length - 1))) != 0) ? raw - (1 << Length) : raw;
        }

        /// <summary>Enum option index (into Options) for the current bits: SignedGet - EnumBase. For a normal
        /// 0-based enum this is just the value; for a signed enum it shifts negatives up into 0-based indices.</summary>
        public int EnumIndex(ushort vars) => SignedGet(vars) - EnumBase;

        /// <summary>The signed game value option <paramref name="index"/> maps to (EnumBase + index). Pass this
        /// to <see cref="Set"/> — its masking produces the correct two's-complement bits for negative values.</summary>
        public int EnumValueAt(int index) => EnumBase + index;

        /// <summary>For a Message field: the in-game textId for a stored bit value (TextIdBase + value).</summary>
        public int TextId(ushort vars) => TextIdBase + Get(vars);
    }

    /// <summary>The decoded logic schema for one actor id.</summary>
    public sealed record Def(string Title, IReadOnlyList<Field> Fields, string? Note = null);

    // Item_DropCollectible index → contents, for the low-5-bit "drop" field on pots/crates/chests.
    private static readonly string[] Drop =
    [
        "Green Rupee (1)", "Blue Rupee (5)", "Red Rupee (20)", "Recovery Heart", "Bombs (5)",
        "Arrows (single)", "Heart Piece(?)", "Magic (small)", "Magic (large)", "Deku Stick (1)",
        "Deku Nuts (5)", "Deku Stick (1) alt", "Magic Arrow(?)", "Recovery Heart alt", "Arrows (10)",
        "Arrows (30)", "Arrows (?)", "Deku Seeds (5)", "Nothing", "Flexible / table-defined",
    ];

    private static readonly string[] BoxType =
    [
        "Big — default", "Big — appear on room clear", "Big — decorated (boss key)",
        "Big — falling, on switch flag", "Big — type 4 (alt draw)", "Small — default",
        "Small — type 6 (alt draw)", "Small — appear on room clear", "Small — falling, on switch flag",
        "Big — type 9", "Big — type 10", "Big — appear on switch flag",
    ];

    // Bg_Ydan_Sp web type (z_bg_ydan_sp.c BgYdanSpType) — bits [12:15].
    private static readonly string[] WebType =
    [
        "Floor web (horizontal, drop-through / burn)", "Wall web (vertical, burnable)",
    ];

    // Item00Type (z64actor.h) — the freestanding En_Item00 type, indexed by the params low byte. Each
    // renders as that item's spinning 3D model in vanilla gameplay. (SOH-only 0x1B–0x1D omitted; those
    // need the fork's GetItemEntry override to show an arbitrary item.)
    private static readonly string[] Item00Type =
    [
        /* 0x00 */ "Green Rupee (1)", "Blue Rupee (5)", "Red Rupee (20)", "Recovery Heart", "Bombs",
        /* 0x05 */ "Arrow (single)", "Piece of Heart", "Heart Container", "Arrows (small)", "Arrows (medium)",
        /* 0x0A */ "Arrows (large)", "Bombs (B)", "Deku Nuts", "Deku Stick", "Magic Jar (large)",
        /* 0x0F */ "Magic Jar (small)", "Deku Seeds", "Small Key", "Flexible (table-defined)", "Orange/Huge Rupee (200)",
        /* 0x14 */ "Purple Rupee (50)", "Deku Shield", "Hylian Shield", "Zora Tunic", "Goron Tunic",
        /* 0x19 */ "Bombs (special)", "Bombchu",
    ];

    // Item_Etcetera (z_item_etcetera.c) — a vanilla freestanding item actor that draws a GetItem (GI)
    // model and gives the item on pickup. Indexed by params low byte. Types 0x00–0x07 give a real item;
    // 0x08–0x0D draw the model but give nothing (GI_NONE — used for scripted/decorative placements).
    private static readonly string[] ItemEtcType =
    [
        /* 0x00 */ "Bottle (empty)", "Ruto's Letter", "Hylian Shield", "Quiver (40 arrows)", "Silver Scale",
        /* 0x05 */ "Golden Scale", "Small Key", "Fire Arrow",
        /* 0x08 */ "Green Rupee (no item)", "Blue Rupee (no item)", "Red Rupee (no item)",
        /* 0x0B */ "Purple Rupee (no item)", "Piece of Heart (no item)", "Small Key (no item)",
    ];

    private static readonly string[] SwitchType =
        ["Floor", "Floor (rusty)", "Eye", "Crystal", "Crystal (targetable)"];

    private static readonly string[] SwitchSubtype =
        ["Once", "Toggle", "Hold", "Hold (inverted)", "Sync"];

    private static readonly string[] DoorType =
        ["Scene-dependent (0)", "Locked (small key)", "Type 2", "Type 3", "Ajar (slams shut)",
         "Checkable (textbox)", "Evening (Dampé)"];

    private static readonly string[] TorchType =
        ["Permanent (stays lit)", "Timed (burns out)", "Decorative (no flag)"];

    private static readonly string[] EnGSwitchType =
        ["Silver tracker (invisible)", "Silver rupee", "Archery pot", "Target rupee"];

    private static readonly string[] MmSwitchType =
        ["Floor", "Floor (rusty)", "Eye", "Crystal", "Crystal (targetable)", "Floor (large)"];

    private static readonly string[] LightswitchType =
        ["Sun switch (regular)", "Flip — Stone Tower inversion", "Type 2", "Fake"];

    // actor id → schema. Curated set of the most logic-bearing OoT actors; extend freely.
    private static readonly Dictionary<ushort, Def> OoT = new()
    {
        // En_Box (treasure chest) — z_en_box.c: type=[12,4], item=[5,7], treasureFlag=[0,5]; switchFlag is Rot Z.
        // Bg_Ydan_Sp — z_bg_ydan_sp.c: type=[12,4] (WEB_FLOOR/WEB_WALL), burnSwitchFlag=[6,6],
        // isDestroyedSwitchFlag=[0,6]. The editor renders the matching floor/wall web (ActorModelResolver).
        [0x000F] = new Def("Cobweb (Bg_Ydan_Sp)", [
            new Field("Web type", 12, 4, FieldKind.Enum, "Floor web = horizontal, drop through from a height / burn; wall web = vertical, burn with a Deku stick", WebType),
            new Field("Burn switch flag", 6, 6, FieldKind.Int, "When this switch flag is set the web burns away (0–63; also settable by fire/Deku stick in-game)",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
            new Field("Destroyed switch flag", 0, 6, FieldKind.Int, "Set when the web is destroyed (and read at load to start already gone) (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Wall web faces along its Y rotation. Both webs use object_ydan_objects (Deku Tree)."),

        // Bg_Haka_Megane — z_bg_haka_megane.c: Lens-of-Truth fake walls/floors/holes. params picks the piece;
        // params<3 = Bottom of the Well (object_hakach_objects), else = Shadow Temple (object_haka_objects).
        [0x00AE] = new Def("Lens Fake Wall/Floor (Bg_Haka_Megane)", [
            new Field("Piece", 0, 8, FieldKind.Int, "Which fake-wall/floor/hole piece (0–2 = Bottom of the Well; 3+ = Shadow Temple)"),
        ], "Normally invisible without the Lens of Truth — the editor draws it so you can place it."),

        // Bg_Mizu_Bwall — z_bg_mizu_bwall.c: wall piece = params&0xF (5 breakable-wall shapes).
        [0x01BA] = new Def("Water Temple Breakable Wall (Bg_Mizu_Bwall)", [
            new Field("Wall piece", 0, 4, FieldKind.Int, "Which of the 5 breakable-wall shapes (0–4)"),
        ], "Loads from object_mizu_objects."),

        // Bg_Spot09_Obj — z_bg_spot09_obj.c: params picks a Gerudo Valley bridge state or the carpenters' tent.
        [0x00B8] = new Def("Gerudo Valley Bridge/Tent (Bg_Spot09_Obj)", [
            new Field("Object", 0, 8, FieldKind.Enum, "Which Gerudo Valley structure. NOTE: 'Suspension bridge (sides)' (0) only appears at scene setup ≥ 4 and won't spawn otherwise — a fresh actor defaults to the tent so it's visible; pick a specific structure here.",
                      new[] { "Suspension bridge (sides)", "Broken bridge", "Bridge (child era)", "Carpenters' tent", "Repaired bridge" }),
        ], "Loads from object_spot09_obj; the bridges are world-scale, the tent is small. Object 0 = don't-spawn-unless-setup≥4 (that is why a default-0 one is invisible)."),

        // Bg_Bdan_Objects — z_bg_bdan_objects.c: params&0xFF picks the Jabu mechanism model.
        [0x00C8] = new Def("Jabu Mechanism (Bg_Bdan_Objects)", [
            new Field("Object", 0, 8, FieldKind.Enum, "Which Jabu-Jabu structure",
                      new[] { "Rotating spike platform", "Elevator platform", "Water surface" }),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Switch flag driving its motion (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Loads from object_bdan_objects (Jabu-Jabu's Belly)."),

        // Obj_Hsblock — z_obj_hsblock.c: params&3 = 0/1 hookshot post, 2 hookshot target.
        [0x012D] = new Def("Hookshot Post/Target (Obj_Hsblock)", [
            new Field("Kind", 0, 2, FieldKind.Enum, "A climbable hookshot post, or a hookshot target",
                      new[] { "Post", "Post", "Target" }),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Switch flag (for the sinking-post type) (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Loads from object_d_hsblock."),

        // Bg_Hidan_Kowarerukabe — z_bg_hidan_kowarerukabe.c: form = params&0xFF (0=cracked floor, 1=bombable
        // wall, 2=large bombable wall); switch flag = params>>8&0x3F set when destroyed.
        [0x00CF] = new Def("Fire Temple Bomb Wall (Bg_Hidan_Kowarerukabe)", [
            new Field("Form", 0, 8, FieldKind.Enum, "Which bomb-breakable — a cracked floor you drop through, or a bombable wall",
                      new[] { "Cracked stone floor", "Bombable wall", "Large bombable wall" }),
            new Field("Destroyed switch flag", 8, 6, FieldKind.Int, "Set when destroyed (and read at load to start already broken) (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Loads from object_hidan_objects (Fire Temple)."),

        // En_Ishi — z_en_ishi.c: size = params&1 (0=small throwable, 1=large silver rock). Model + scale differ.
        [0x014E] = new Def("Rock (En_Ishi)", [
            new Field("Rock size", 0, 1, FieldKind.Enum, "Small throwable rock, or the large silver rock (needs Silver Gauntlets to lift)",
                      new[] { "Small rock", "Large silver rock" }),
            new Field("Drop table", 8, 8, FieldKind.Int, "Item-drop table index used when smashed (0 = default)"),
        ], "Both forms load from gameplay_field_keep."),

        // En_Vm (Beamos) — z_en_vm.c:148-157: beamSightRange = (params>>8)*40 units; low byte = size
        // (0 BEAMOS_LARGE, 1 BEAMOS_SMALL). With the default params 0 the sight range is 0, so the beamos is
        // BLIND and never aggros — the reported "won't wake up" bug (nothing to do with collision/space).
        [0x008A] = new Def("Beamos (En_Vm)", [
            new Field("Size", 0, 8, FieldKind.Enum, "Large or small beamos body", new[] { "Large", "Small" }),
            new Field("Sight range (×40 units)", 8, 8, FieldKind.Int,
                      "Detection radius in game units = this value × 40. MUST be > 0 or the beamos is blind and never aggros (0 is why a fresh one ignores you). ~15 ≈ 600u covers a room; SoH's rando uses 5 (200u)."),
        ], "Detects Link within sight range AND a vertical band (~80u above to 160u below the head); rotates its beam to fire. beamSightRange = (params>>8)*40, size = params&0xFF."),

        [0x000A] = new Def("Treasure Chest", [
            new Field("Chest type", 12, 4, FieldKind.Enum, "Size, appearance and spawn condition", BoxType),
            new Field("Contents", 5, 7, FieldKind.Enum, "Item given when the chest is opened (None = empty chest)", GetItemTable.OoT),
            new Field("Treasure flag", 0, 5, FieldKind.Int, "Chest-contents flag (0–31); marks this chest as opened",
                      Flag: FlagKind.Chest, Role: FlagRole.Both),
            new Field("Switch flag (Rot Z)", 0, 6, FieldKind.Int, "Switch-flag chest types (3/8 falling, 11) read this switch flag — stored in Rot Z, not params — to appear when it's set",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader, FromRotZ: true),
        ], "Switch flag (for switch-flag chest types) is stored in Rot Z of the transform."),

        // En_G_Switch (0x0117) — z_en_g_switch.c: type=(params>>12)&0xF, silverCount=(params>>6)&0x3F,
        // switchFlag=params&0x3F. type 0 = silver-rupee TRACKER (invisible; sets the switch flag once
        // silverCount rupees are collected, then dies), type 1 = one silver RUPEE (shares the flag so it
        // doesn't respawn once solved). (The collected count is a shared global → one silver puzzle per room.)
        [0x0117] = new Def("Silver Rupee / Tracker (En_G_Switch)", [
            new Field("Role", 12, 4, FieldKind.Enum, "Tracker counts the rupees + opens the gate; Silver rupee is one collectible", EnGSwitchType),
            new Field("Silver count", 6, 6, FieldKind.Int, "Tracker only: how many silver rupees to collect (usually 5)"),
            new Field("Switch flag", 0, 6, FieldKind.Int, "Tracker SETS this when all rupees are collected; the rupees + a gate share it (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Place ONE tracker + N silver rupees on the same switch flag, then wire a gate/door to that flag."),

        // En_Am (0x0054) — z_en_am.c: params 0 == ARMOS_STATUE (dormant BG statue, ACTOR_FLAG_26); any other
        // value = a sleeping Armos enemy. The statue is heavy enough to hold a floor switch down (DYNA_INTERACT_3).
        [0x0054] = new Def("Armos (En_Am)", [],
            "Variable 0 = a dormant STATUE: a heavy pushable BG object (ACTOR_FLAG_26) you can shove onto a floor switch to hold it down. Any nonzero value spawns a sleeping Armos ENEMY instead."),

        // Item_B_Heart (0x005F) — z_item_b_heart.c: no params; the boss Heart Container (collectible flag 0x1F,
        // hardcoded). Kills itself if already collected. Object OBJECT_GI_HEARTS.
        [0x005F] = new Def("Heart Container (Item_B_Heart)", [],
            "The big boss-reward Heart Container. No parameters (uses the fixed collectible flag 0x1F); vanishes once collected."),

        // Obj_Syokudai (0x005E) — z_obj_syokudai.c: switchFlag=params&0x3F, torchCount=(params>>6)&0xF,
        // startLit=params&0x400, torchType=params&0xF000 (>>0xC picks stand collision 0/1/2). torchCount 0 =
        // this torch sets the flag by itself when lit; N = the flag sets once N torches (sLitTorchCount) are lit.
        // torchType 0 stays lit permanently (a latching gate trigger); type 2 never sets a flag (decorative).
        [0x005E] = new Def("Torch (Obj_Syokudai)", [
            new Field("Torch type", 12, 2, FieldKind.Enum, "Permanent stays lit; Timed burns out; Decorative sets no flag", TorchType),
            new Field("Start lit", 10, 1, FieldKind.Flag, "Begins already lit"),
            new Field("Torch group size", 6, 4, FieldKind.Int, "0 = this torch sets the flag alone; N = flag sets once N torches in the group are lit"),
            new Field("Switch flag", 0, 6, FieldKind.Int, "Scene switch flag set when lit (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Setter),
        ], "Light it (Din's Fire / fire arrow / a lit Deku stick) to set its switch flag — wire a gate/door to that flag."),

        // Obj_Switch — z_obj_switch.c: type=[0,3], subtype=[4,3], frozen=[7,1], switchFlag=[8,6].
        [0x012A] = new Def("Switch", [
            new Field("Switch type", 0, 3, FieldKind.Enum, "Floor / eye / crystal switch", SwitchType),
            new Field("Behaviour", 4, 3, FieldKind.Enum, "How the switch latches", SwitchSubtype),
            new Field("Frozen in ice", 7, 1, FieldKind.Flag, "Encased in ice until melted"),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Scene switch flag this sets when pressed (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Setter),
        ]),

        // Obj_Tsubo (pot) — z_obj_tsubo.c: drop=[0,5], collectibleFlag=[9,6], object=[8,1].
        [0x0111] = new Def("Pot", [
            new Field("Drop contents", 0, 5, FieldKind.Enum, "What the pot drops when broken", Drop),
            new Field("Object set", 8, 1, FieldKind.Int, "Which pot object/appearance (0–1)"),
            new Field("Collectible flag", 9, 6, FieldKind.Int, "Flag tracking this drop as collected (0–63)",
                      Flag: FlagKind.Collectible, Role: FlagRole.Both),
        ]),

        // Obj_Kibako (small crate) — z_obj_kibako.c: collectible=[0,5], flag=[8,6].
        [0x0110] = new Def("Small Crate", [
            new Field("Drop contents", 0, 5, FieldKind.Enum, "What the crate drops when broken", Drop),
            new Field("Collectible flag", 8, 6, FieldKind.Int, "Flag tracking this drop as collected (0–63)",
                      Flag: FlagKind.Collectible, Role: FlagRole.Both),
        ]),

        // En_Item00 (free-standing collectible) — z_en_item00.c: type=[0,8] (Item00Type), flag=[8,6], 0x8000 spawn.
        // This IS the editor's "freestanding item": it shows the chosen item's spinning model in-world (vanilla).
        [0x0015] = new Def("Freestanding Item (En_Item00)", [
            new Field("Item", 0, 8, FieldKind.Enum, "Freestanding item; renders as its spinning 3D model in-world (vanilla En_Item00 types)", Item00Type),
            new Field("Collectible flag", 8, 6, FieldKind.Int, "Flag tracking this pickup as collected (0–63)",
                      Flag: FlagKind.Collectible, Role: FlagRole.Both),
            new Field("Falling/arcing drop", 15, 1, FieldKind.Flag, "0x8000: spawn as a thrown/falling drop (NOT a respawn condition — the uncollected check is always applied)"),
        ], "Item types 0x1B–0x1D (show an ARBITRARY GetItem) need the SoH/2Ship fork's GetItemEntry override; the listed types work in vanilla."),

        // Item_Etcetera (0x010F) — z_item_etcetera.c: the OTHER vanilla freestanding item. Draws a GI
        // model and gives a curated item on pickup. type=[0,8] (ItemEtcType), treasure flag=[8,5].
        // Covers items En_Item00 can't (bottle, Hylian shield, scales, quiver, fire arrow, Ruto's letter).
        [0x010F] = new Def("Freestanding Item (Item_Etcetera)", [
            new Field("Item", 0, 8, FieldKind.Enum, "Freestanding GetItem shown as its spinning model; types 0x00–0x07 give the item, 0x08+ are model-only (no item)", ItemEtcType),
            new Field("Treasure flag", 8, 5, FieldKind.Int, "Collectible/treasure flag (0–31) tracking this item as taken",
                      Flag: FlagKind.Chest, Role: FlagRole.Both),
        ], "Vanilla actor — complements En_Item00 with items it can't show (bottle, shield, scales, quiver…)."),

        // En_Door — z_en_door.c: type=[7,3]; transition-actor index=[10,6]; low 6 bits = switch flag
        // (locked door) OR text id (checkable). Locked doors read+set the switch flag.
        [0x0009] = new Def("Door", [
            new Field("Door type", 7, 3, FieldKind.Enum, "Behaviour (locked, ajar, checkable, evening…)", DoorType),
            new Field("Transition index", 10, 6, FieldKind.Int, "Index into the scene's transition-actor list (room link)"),
            new Field("Switch flag (locked) / text id", 0, 6, FieldKind.Int, "Locked doors read this switch flag to stay barred and set it when unlocked; checkable doors reuse it as a text id",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Doors are transition actors: the transition index ties the two rooms it connects in the scene header."),

        // Door_Shutter — z_door_shutter.c: transition index=[10,6], doorType=[6,4], switchFlag=[0,6].
        [0x002E] = new Def("Shutter Door", [
            new Field("Transition index", 10, 6, FieldKind.Int, "Index into the scene's transition-actor list (room link)"),
            new Field("Door type", 6, 4, FieldKind.Int, "1=opens on room clear, 2/7=follows a switch flag, 5=boss, 0xB=key-locked"),
            new Field("Switch flag", 0, 6, FieldKind.Int, "FRONT_SWITCH unbars while this switch flag is set; KEY_LOCKED/BOSS set it on open (0–63; 0x3F=none)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Type 1 (FRONT_CLEAR) opens on the ROOM-CLEAR flag (defeat all enemies in this room) — that is a room-indexed clear flag, not a switch flag."),

        // En_Holl — z_en_holl.c: transition-actor index=[10,6] (room-load planes).
        [0x0023] = new Def("Room-Load Plane (En_Holl)", [
            new Field("Transition index", 10, 6, FieldKind.Int, "Index into the scene's transition-actor list (room link)"),
        ], "Invisible plane that triggers loading the connected room as Link passes through."),

        // #4: En_Ossan — one actor that becomes any shopkeeper; params == OSSAN_TYPE picks the body model
        // AND the shop stock. The editor's ActorModelResolver already loads the matching body per value, so
        // this dropdown drives both the rendered variant and the shop. Order verified vs z_en_ossan.c.
        [0x003D] = new Def("Shopkeeper (En_Ossan)", [
            new Field("Shopkeeper", 0, 4, FieldKind.Enum, "Which shop/keeper — sets the body model and stock", [
                "Kokiri Shop", "Kakariko Potion Shop", "Bombchu Shop", "Market Potion Shop", "Bazaar",
                "Market (adult)", "Talon", "Zora Shop", "Goron Shop", "Ingo", "Happy Mask Salesman",
            ]),
        ], "A composite NPC: the whole params value is the shopkeeper type."),

        // #4: En_Hy — generic Hylian townsfolk; type (params & 0x7F) picks the body skeleton + head, path
        // in bits [10:7]. The editor renders the matching body per choice. Names from z_en_hy.c sModelInfo.
        [0x016E] = new Def("Townsperson (En_Hy)", [
            new Field("Townsperson", 0, 7, FieldKind.Enum, "Which townsfolk body/face this is", [
                "Dog Lady", "Bombchu Bowling Lady", "Old Woman", "Hylian Man (dark)", "Old Man",
                "Blue Fire Man", "Old Woman (alt)", "Poe Collector", "Young Woman (brown hair)",
                "Hylian Man (red)", "Hylian Man (blue)", "Young Woman (orange hair)", "Carpenter (night)",
                "Old Woman (noble)", "Hylian Man (alley)", "Poe Collector (alt)", "Hylian Man (guard)",
                "Lost Woods Woman", "Carpenter (day)", "Poe Collector (child)", "Old Woman (child)",
            ]),
            new Field("Path", 7, 4, FieldKind.Int, "Path index this townsperson walks (0 = stationary)"),
        ], "A composite NPC: the body skeleton + head are chosen from object_aob/boj/ahg/bba/cne/bji/cob/bob by type."),

        // #4: En_Ko — Kokiri children; type (params & 0xFF) picks a boy/girl/Fado body. Path in bits [15:8].
        [0x0163] = new Def("Kokiri Child (En_Ko)", [
            new Field("Kokiri", 0, 8, FieldKind.Enum, "Which Kokiri child (body model)", [
                "Boy 1", "Girl 1", "Boy 2", "Boy 3", "Boy 4", "Girl (shop)", "Girl 2",
                "Boy 5", "Boy 6", "Girl 3", "Girl 4", "Boy 7", "Fado",
            ]),
            new Field("Path", 8, 8, FieldKind.Int, "Path index this Kokiri walks (0 = stationary)"),
        ]),

        // #4: En_Dnt_Nomal — Lost Woods scrub minigame: stage scrub (object_dnk) vs target scrub (hintnuts).
        [0x01A3] = new Def("Lost Woods Scrub (En_Dnt_Nomal)", [
            new Field("Scrub", 0, 8, FieldKind.Enum, "Stage scrub or a pop-up target scrub", ["Stage Scrub", "Target Scrub"]),
        ]),

        // #4: En_Wood02 — tree / bush / leaves; type (params & 0xFF) chooses the display list. Ranges:
        // 0-4 conical tree, 5-9 oval tree, 0xA Kakariko tree, 0xB-0x10 green bush, 0x11-0x17 dark bush/
        // leaves, 0x18 yellow leaves (the value within a range tweaks size/tint).
        [0x0077] = new Def("Tree / Bush (En_Wood02)", [
            new Field("Type", 0, 8, FieldKind.Int, "0-4 conical tree · 5-9 oval tree · 10 Kakariko tree · 11-16 green bush · 17-23 dark bush/leaves · 24 yellow leaves"),
        ]),

        // En_Sw (0x0095) is the GOLD SKULLTULA TOKEN actor (not a switch/counter):
        // z_en_sw.c reads GET_GS_FLAGS((params&0x1F00)>>8) & (params&0xFF). Corrected per the decomp.
        [0x0095] = new Def("Gold Skulltula Token", [
            new Field("GS flag group", 8, 5, FieldKind.Int, "Which gold-skulltula flag group (scene group, 0–31)",
                      Flag: FlagKind.GoldSkulltula, Role: FlagRole.Reader),
            new Field("GS flag bit", 0, 8, FieldKind.Int, "Bit within the group identifying this token"),
        ], "The skulltula you strike for a token. Self-kills if its GS flag is already collected. (This is NOT a generic switch/counter — earlier schema versions mislabelled it.)"),

        // Obj_Bombiwa (bombable boulder) — z_obj_bombiwa.c: switchFlag=[0,6], dropOnBreak=[15,1].
        [0x0127] = new Def("Bombable Boulder", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Reads it to stay hidden; sets it when destroyed (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
            new Field("Drop on break", 15, 1, FieldKind.Flag, "Spawn a collectible when destroyed"),
        ]),

        // Obj_Hamishi (large rock) — z_obj_hamishi.c: switchFlag=[0,6].
        [0x01D2] = new Def("Large Rock (Obj_Hamishi)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Reads it to stay hidden; sets it when destroyed (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // En_MhTalk — Megaton Hammer's own portable "dialogue point" (register the ovl_En_MhTalk overlay at
        // actor id 0x0230 in your base; see portable/README.md). params (low 8 bits) = a dialogue slot; the
        // entry textId is 0x1000 + slot. This is the general way to give ANY NPC / sign / point full custom
        // dialogue + outcomes (existing NPCs like Talon have no message param — their dialogue is hardcoded).
        [ActorDatabase.MhTalkId] = new Def("Dialogue Point (En_MhTalk)", [
            new Field("Message", 0, 8, FieldKind.Message, "The conversation shown when talked to (textId 0x1000 + value) — click Edit to author it", TextIdBase: 0x1000),
        ], "Megaton Hammer's fork-independent talk actor. Place it (beside an NPC, on a sign, or as its own point) and author the whole conversation + outcomes in the Dialogue Editor. Export writes the message bytes + mh_dialogue_data.c."),

        // Elf_Msg (invisible trigger region) — z_elf_msg.c: messageId=[0,8], switchFlag=[8,6] (0x3F = none).
        // Elf_Msg (invisible message/inspect region) — z_elf_msg.c: textId=(params&0xFF)+0x100 when bit
        // 0x8000 set (message mode); switchFlag=[8,6].
        [0x011B] = new Def("Message Region (Elf_Msg)", [
            new Field("Message", 0, 8, FieldKind.Message, "Dialogue shown when entered (textId 0x100 + value)", TextIdBase: 0x100),
            new Field("Show message", 15, 1, FieldKind.Flag, "0x8000: act as a message region (off = effect/data region)"),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Switch flag it sets/reads; 0x3F (63) = none",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // Elf_Msg2 (invisible message region) — z_elf_msg2.c: textId=(params&0xFF)+0x100; switchFlag=[8,6].
        [0x0173] = new Def("Message Region (Elf_Msg2)", [
            new Field("Message", 0, 8, FieldKind.Message, "Dialogue shown when entered (textId 0x100 + value)", TextIdBase: 0x100),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Switch flag it sets/reads; 0x3F (63) = none",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // En_Kanban (sign) — z_en_kanban.c: textId = params | 0x300 (low bits select the sign text).
        [0x0141] = new Def("Sign (En_Kanban)", [
            new Field("Message", 0, 8, FieldKind.Message, "Sign dialogue (textId 0x300 + value)", TextIdBase: 0x300),
        ], "Special params: 0x100 = fishing-pond sign (fixed text), 0xFF00 high byte 'piece' = a cut sign fragment."),

        // En_Wonder_Talk2 (inspect / point-of-interest text) — z_en_wonder_talk2.c:
        // textId = 0x200 | ((params>>6)&0xFF); switchFlag = params&0x3F; talk mode = (params>>14)&3.
        [0x0185] = new Def("Point of Interest (En_Wonder_Talk2)", [
            new Field("Message", 6, 8, FieldKind.Message, "Dialogue shown on inspect (textId 0x200 + value)", TextIdBase: 0x200),
            new Field("Switch flag", 0, 6, FieldKind.Int, "Switch flag it sets/reads; 0x3F (63) = none",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
            new Field("Talk mode", 14, 2, FieldKind.Enum, "How it triggers",
                      ["Talk + set switch", "Talk + set switch (alt)", "Repeatable talk", "Silent"]),
        ], "Proximity trigger range is encoded in ROT Z (not params): range = (Rot Z's ones digit)×40 units, tens digit = target style. Rot Z MUST be > 0 or it NEVER fires — a fresh one defaults Rot Z to 10 (400u)."),

        // Obj_Kibako2 (large crate) — z_obj_kibako2.c: itemDrop in params; collectibleFlag = Rot Z & 0x3F.
        [0x01A0] = new Def("Large Crate", [
            new Field("Drop contents", 0, 8, FieldKind.Int, "Item dropped when broken"),
            new Field("Collectible flag (Rot Z)", 0, 6, FieldKind.Int, "Flag tracking this crate's drop as collected — stored in Rot Z (& 0x3F), not params (0 = none)",
                      Flag: FlagKind.Collectible, Role: FlagRole.Both, FromRotZ: true),
        ], "Collectible flag is stored in Rot Z (& 0x3F) of the transform, not in params."),

        // Bg_Mizu_Water (Water Temple water-level controller) — z_bg_mizu_water.c: type=[0,8], switchFlag=[8,8].
        [0x0065] = new Def("Water-Level Controller (Water Temple)", [
            new Field("Type", 0, 8, FieldKind.Int, "0 = main water level"),
            new Field("Base switch flag", 8, 8, FieldKind.Int, "Reads switch flags 0x1C/0x1D/0x1E for the three levels",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
        ], "Rewrites the waterboxes' surface Y from the three Triforce switch flags (F1 0x1C, F2 0x1D, F3 0x1E)."),

        // ── Additional switch/clear/logic actors (verified ids + bit layouts from the OoT decomp) ──

        // Bg_Breakwall (0x0059) — bombable wall/boulder: switchFlag = params & 0x3F (read to stay broken, set on break).
        [0x0059] = new Def("Bombable Wall (Bg_Breakwall)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Read to stay destroyed; set when broken (0–63; 0x3F=none)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
            new Field("Wall type", 6, 4, FieldKind.Int, "Shape/behaviour of the breakable surface"),
        ], "Some variants also fire a one-time cutscene (e.g. Dodongo's Cavern intro) gated on an event flag."),

        // Bg_Mizu_Movebg (0x0064) — Water Temple moving platform: switchFlag = params & 0x3F.
        [0x0064] = new Def("Moving Platform (Water Temple)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Reads this switch flag to choose/trigger its position (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
            new Field("Type", 6, 4, FieldKind.Int, "Which moving-bg behaviour"),
        ]),

        // Bg_Hidan_Kousi (0x006F) — Fire Temple grate: switchFlag = (params>>8)&0xFF (8-bit).
        [0x006F] = new Def("Grate / Barrier (Fire Temple)", [
            new Field("Switch flag", 8, 8, FieldKind.Int, "Slides open while this switch flag is set (reader; 8-bit)",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
        ]),

        // Door_Toki (0x0070) — Door of Time: toggles its own collision on EVENTCHKINF_OPENED_THE_DOOR_OF_TIME.
        [0x0070] = new Def("Door of Time (Door_Toki)", [],
            "Opens based on the 'opened Door of Time' story event flag (eventChkInf) — it does not start the cutscene itself."),

        // Bg_Hidan_Hamstep (0x0071) — Fire Temple hammer block: switchFlag = (params>>8)&0xFF (8-bit), read+set.
        [0x0071] = new Def("Hammer Block (Fire Temple)", [
            new Field("Switch flag", 8, 8, FieldKind.Int, "Read and set on hammer hit (8-bit)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // Bg_Mori_Elevator (0x0087) — Forest Temple elevator: switchFlag = params & 0x3F.
        [0x0087] = new Def("Elevator (Forest Temple)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Reads this switch flag to choose raised/lowered + trigger motion (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
        ]),

        // Bg_Po_Event (0x0093) — Forest Temple poe/painting puzzle: type=[8,4], index=[12,4], switchFlag=[0,6].
        [0x0093] = new Def("Poe / Painting Puzzle (Bg_Po_Event)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Puzzle switch flag (read to skip if solved; set on solve)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
            new Field("Type", 8, 4, FieldKind.Int, "Which poe/painting element"),
            new Field("Index", 12, 4, FieldKind.Int, "Element index within the puzzle"),
        ]),

        // Bg_Bdan_Switch (0x00E6) — Jabu-Jabu switches: type=[0,8], switchFlag=[8,6].
        [0x00E6] = new Def("Switch (Jabu-Jabu)", [
            new Field("Switch type", 0, 8, FieldKind.Int, "Blue / yellow / heavy / tall switch"),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Switch flag set/read (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // Obj_Lift (0x012C) — collapsing platform: switchFlag = (params>>2)&0x3F.
        [0x012C] = new Def("Collapsing Platform (Obj_Lift)", [
            new Field("Switch flag", 2, 6, FieldKind.Int, "Self-removes if set; sets it when it collapses (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // Obj_Hsblock (0x012D) — hookshot post/block: subtype=[0,8], switchFlag=[8,6].
        [0x012D] = new Def("Hookshot Post / Sinking Block (Obj_Hsblock)", [
            new Field("Subtype", 0, 8, FieldKind.Int, "Post vs sinking block behaviour"),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Switch flag read/set (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // En_Owl (0x014D) — owl: owlType=[6,6], switchFlag=[0,6].
        [0x014D] = new Def("Owl (En_Owl)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Read to despawn after talking; set after talking (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
            new Field("Owl type", 6, 6, FieldKind.Int, "Which owl conversation / location"),
        ]),

        // Bg_Jya_Lift (0x0157) — Spirit Temple lift: switchFlag = params & 0x3F.
        [0x0157] = new Def("Lift (Spirit Temple)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Reads this switch flag to raise/lower (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
        ]),

        // Bg_Jya_1flift (0x018E) — Spirit Temple 1F lift: switchFlag = params & 0x3F.
        [0x018E] = new Def("1F Lift (Spirit Temple)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Reads this switch flag to raise/lower (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
        ]),

        // Demo_Kekkai (0x01A7) — Ganon's Castle barriers: barrier enum in params; gated on event flags.
        [0x01A7] = new Def("Barrier (Demo_Kekkai)", [
            new Field("Barrier", 0, 8, FieldKind.Int, "Which Ganon's-Castle trial barrier / tower barrier"),
        ], "Self-removes when its story (eventChkInf) flag is set; trial barriers clear on the matching sage's light-arrow cutscene."),

        // En_Gs (0x01B9) — gossip stone: text=[0,8], switchFlag=[8,6].
        [0x01B9] = new Def("Gossip Stone (En_Gs)", [
            new Field("Text / data", 0, 8, FieldKind.Int, "Gossip message id offset (+0x400)"),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Switch flag read/set for one-time hints (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // Obj_Timeblock (0x01D1) — Song of Time block: switchFlag = params & 0x3F (read+set).
        [0x01D1] = new Def("Song of Time Block (Obj_Timeblock)", [
            new Field("Switch flag", 0, 6, FieldKind.Int, "Read/set switch flag controlling whether the block is present (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ]),

        // En_Blkobj (Dark Link illusory-room prop) — no logic params; placement only.
        [0x0136] = new Def("Dark Link Illusion Room (En_Blkobj)", [],
            "Renders the mirror room's real/illusion meshes and its collision; clears when Dark Link (En_Torch2, 0x0033) is defeated."),

        // ── Common enemies + the push block (type/variant params; each verified vs decomp) ──

        // Obj_Oshihiki (0x00FF) push block — z_obj_oshihiki.c: size=params&0xF, colour=(params>>6)&3, switchFlag=(params>>8)&0x3F.
        [0x00FF] = new Def("Push Block (Obj_Oshihiki)", [
            new Field("Block", 0, 4, FieldKind.Enum, "Size sets the strength needed to move it; START_OFF variants begin in their lowered/retracted state",
                      ["Small", "Medium", "Large — needs Goron Bracelet", "Huge — needs Silver Gauntlets",
                       "Small (start off)", "Medium (start off)", "Large (start off)"]),
            new Field("Colour variant", 6, 2, FieldKind.Int, "Block tint (0–3)"),
            new Field("Switch flag", 8, 6, FieldKind.Int, "Blocks/floor switches sharing this flag sync their state (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Large needs the Goron Bracelet; Huge needs the Silver Gauntlets (Player_GetStrength check in z_obj_oshihiki.c)."),

        // En_Firefly (0x0013) Keese — z_en_firefly.c: type=params (0–4); bit 0x8000 = invisible (Lens of Truth).
        [0x0013] = new Def("Keese (En_Firefly)", [
            new Field("Type", 0, 3, FieldKind.Enum, "Element + whether it starts flying or perched",
                      ["Fire (flying)", "Fire (perched)", "Normal (flying)", "Normal (perched)", "Ice (flying)"]),
            new Field("Invisible (Lens)", 15, 1, FieldKind.Flag, "Only visible with the Lens of Truth"),
        ]),

        // En_Poh (0x000D) Poe — z_en_poh.c: type=params (0–3); Sharp/Flat are the Composer Brothers (ReDead ghosts).
        [0x000D] = new Def("Poe (En_Poh)", [
            new Field("Type", 0, 2, FieldKind.Enum, "Poe variant",
                      ["Normal Poe", "Rupee Poe", "Composer — Sharp", "Composer — Flat"]),
        ], "Composer Sharp/Flat hard-code their own story flags (0x28/0x29); Normal/Rupee are the free-roaming Poes."),

        // En_Wf (0x01AF) Wolfos — z_en_wf.c: type=params&0xFF (0 grey / 1 White), switchFlag=(params>>8)&0xFF (0xFF=none).
        [0x01AF] = new Def("Wolfos (En_Wf)", [
            new Field("Type", 0, 8, FieldKind.Enum, "Grey Wolfos, or the White Wolfos mini-boss that locks the room until beaten",
                      ["Grey (normal)", "White (mini-boss)"]),
            new Field("Room-clear switch flag", 8, 8, FieldKind.Int, "White Wolfos SETS this switch flag when defeated (255 = none) — wire a barred door to it",
                      Flag: FlagKind.Switch, Role: FlagRole.Setter),
        ]),

        // En_Kusa (0x0125) grass/bush — z_en_kusa.c: type=params&3, dropGroup=(params>>8)&0xF.
        [0x0125] = new Def("Grass / Bush (En_Kusa)", [
            new Field("Type", 0, 2, FieldKind.Enum, "Cuttable grass tuft, or a bush you can pick up and throw",
                      ["Grass tuft", "Bush (throwable)", "Grass tuft (alt)"]),
            new Field("Drop group", 8, 4, FieldKind.Int, "Which random-drop table it yields when cut (0–12; 13+ = nothing)"),
        ]),

        // Bg_Heavy_Block (0x0092) liftable pillar — z_bg_heavy_block.c: type=params&0xFF, appearSwitchFlag=(params>>8)&0x3F.
        [0x0092] = new Def("Heavy Pillar (Bg_Heavy_Block)", [
            new Field("Type", 0, 8, FieldKind.Enum, "The liftable pillar, or a debris piece it shatters into",
                      ["Pillar (unbreakable)", "Pillar (breakable)", "Big piece", "Small piece", "Pillar (outside castle)"]),
            new Field("Appear switch flag", 8, 6, FieldKind.Int, "Some placements appear only once this switch flag is set (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
        ], "Lift and throw the pillar with the Golden Gauntlets (adult). Big/Small piece are the runtime debris."),

        // Obj_Comb (0x019E) beehive — z_obj_comb.c: drop=params&0x1F (Item00 type), collectibleFlag=(params>>8)&0x3F.
        [0x019E] = new Def("Beehive (Obj_Comb)", [
            new Field("Drop", 0, 5, FieldKind.Enum, "What the hive drops when knocked down", Item00Type),
            new Field("Collectible flag", 8, 6, FieldKind.Int, "Flag tracking this drop as collected (0–63)",
                      Flag: FlagKind.Collectible, Role: FlagRole.Both),
        ]),

        // ── Enemies whose type param is SIGNED (negative sentinels) — decoded via Field.EnumBase ──

        // En_Tite (0x001B) Tektite — z_en_tite.c: whole params == TEKTITE_BLUE(-2) / TEKTITE_RED(-1). No other fields.
        [0x001B] = new Def("Tektite (En_Tite)", [
            new Field("Colour", 0, 16, FieldKind.Enum, "Blue tektites skate on water; red ones hop on land", ["Blue", "Red"], EnumBase: -2),
        ]),

        // En_Zf (0x0025) Lizalfos/Dinolfos — z_en_zf.c: type=s8(params&0xFF, sign-extended), clearFlag=(params>>8)&0xFF.
        [0x0025] = new Def("Lizalfos / Dinolfos (En_Zf)", [
            new Field("Type", 0, 8, FieldKind.Enum, "Dinolfos, a lone Lizalfos, or a paired mini-boss (A+B must both die to clear the room)",
                      ["Dinolfos", "Lizalfos (lone)", "Lizalfos mini-boss A", "Lizalfos mini-boss B"], EnumBase: -2),
            new Field("Room-clear switch flag", 8, 8, FieldKind.Int, "Mini-boss pair reads/sets this so the room unbars when BOTH are beaten (0–63)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Give both mini-boss halves (A and B) the SAME room-clear switch flag."),

        // En_Rd (0x0090) ReDead/Gibdo — z_en_rd.c: type=s8(params&0xFF, sign-extended); high byte = spawn flags.
        [0x0090] = new Def("ReDead / Gibdo (En_Rd)", [
            new Field("Type", 0, 8, FieldKind.Enum, "ReDead or Gibdo variant; some grab, some only moan, some are invisible",
                      ["Gibdo (rising from coffin)", "Gibdo", "ReDead (won't mourn)", "ReDead (won't mourn if walking)",
                       "ReDead (regular)", "ReDead (crying)", "ReDead (invisible)"], EnumBase: -3),
        ]),

        // En_Bb (0x0069) Bubble — z_en_bb.c: type=s8(params&0xFF, sign-extended), flight path=(params>>8)&0xFF.
        [0x0069] = new Def("Bubble (En_Bb)", [
            new Field("Type", 0, 8, FieldKind.Enum, "Flying skull-flame; colour sets its behaviour (Blue is the plain one)",
                      ["Green (big)", "Green", "White", "Red", "Blue", "Flame trail"], EnumBase: -5),
            new Field("Flight path", 8, 8, FieldKind.Int, "Path index the flying variants follow (0–254)"),
        ]),

        // ── More common enemies ──

        // En_Test (0x0002) Stalfos — z_en_test.c: type=params (StalfosType 0–5). NOTE type 0 spawns invisible.
        [0x0002] = new Def("Stalfos (En_Test)", [
            new Field("Type", 0, 3, FieldKind.Enum, "Spawn/behaviour variant. Type 0 is INVISIBLE until triggered — pick another for a plain visible Stalfos",
                      ["Invisible (until triggered)", "Type 1", "Type 2", "Drops from ceiling", "Type 4", "Type 5"]),
        ]),

        // En_Wallmas (0x0011) Wallmaster — z_en_wallmas.c: type=params&0xFF (WMT 0–2), despawnFlag=(params>>8)&0xFF.
        [0x0011] = new Def("Wallmaster (En_Wallmas)", [
            new Field("Trigger", 0, 2, FieldKind.Enum, "When it drops: on a timer, when Link lingers below it, or gated by a switch flag",
                      ["Timer", "Proximity", "Switch-flag gated"]),
            new Field("Despawn switch flag", 8, 8, FieldKind.Int, "'Switch-flag gated' type only: it removes itself if this switch flag is already set (0–63)"),
        ]),

        // En_Dekubaba (0x0055) Deku Baba — z_en_dekubaba.c: type=params (0 normal / 1 big). Withered = En_Karebaba 0x00C7.
        [0x0055] = new Def("Deku Baba (En_Dekubaba)", [
            new Field("Size", 0, 2, FieldKind.Enum, "Big Deku Babas drop a Deku Stick/Nut and need the shield to kill safely",
                      ["Normal", "Big"]),
        ]),

        // En_Reeba (0x001C) Leever — z_en_reeba.c: type=params (0 small / 1 big).
        [0x001C] = new Def("Leever (En_Reeba)", [
            new Field("Size", 0, 2, FieldKind.Enum, "Sand-dwelling Leever", ["Small", "Big"]),
        ]),

        // En_Peehat (0x001D) Peahat — z_en_peehat.c: whole params signed (PeahatType -1..1).
        [0x001D] = new Def("Peahat (En_Peehat)", [
            new Field("Type", 0, 16, FieldKind.Enum, "Grounded Peahats sit until approached; flying ones patrol and spawn larvae",
                      ["Grounded", "Flying", "Larva"], EnumBase: -1),
        ]),
    };

    // MM actor ids differ from OoT and its switch flags are 7 bits (0–127). Layouts taken verbatim
    // from the 2ship/mm decomp. Covers the most logic-bearing actors incl. MM-specific ones.
    private static readonly Dictionary<ushort, Def> MM = new()
    {
        // MM Obj_Switch (0x093) — z_obj_switch.c: type=(params&7), subtype=(params>>4&7). Model per type.
        [0x0093] = new Def("Switch (Obj_Switch)", [
            new Field("Switch type", 0, 3, FieldKind.Enum, "Which switch — floor pad, rusty floor, eye, crystal, or large floor",
                      new[] { "Floor", "Floor (rusty)", "Eye", "Crystal", "Crystal (targetable)", "Floor (large)" }),
            new Field("Subtype", 4, 3, FieldKind.Int, "Behaviour/variant: 0=once, 1=toggle, 2/3=hold, and eye gold/silver"),
            new Field("Switch flag", 8, 7, FieldKind.Int, "Scene switch flag this reads/sets (0–127)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "All forms load from the MM gameplay_dangeon_keep."),

        // ── MM common enemies/objects (verified against mm-main; params DIFFER from the OoT versions) ──

        // En_Dekubaba (MM 0x033) — z_en_dekubaba.c: whole params 0 normal / 1 big (same shape as OoT).
        [0x0033] = new Def("Deku Baba (En_Dekubaba)", [
            new Field("Size", 0, 2, FieldKind.Enum, "Big Deku Babas drop a Deku Stick/Nut", ["Normal", "Big"]),
        ]),

        // En_Kusa (MM 0x090) grass/bush — z_en_kusa.h: KUSA_GET_TYPE=params&3, spawnBugs=(params>>4)&1.
        // NOTE: MM's type ORDER differs from OoT (Bush is 0 here).
        [0x0090] = new Def("Grass / Bush (En_Kusa)", [
            new Field("Type", 0, 2, FieldKind.Enum, "Cuttable grass, or a bush you can pick up and throw",
                      ["Bush (throwable)", "Regrowing grass", "Grass", "Grass (alt)"]),
            new Field("Spawns bugs", 4, 1, FieldKind.Flag, "Releases catchable bugs when cut"),
        ]),

        // Obj_Comb (MM 0x0E4) beehive — z_obj_comb.h: drop=params&0x1F, collectibleFlag=(params>>8)&0x7F (7-bit).
        [0x00E4] = new Def("Beehive (Obj_Comb)", [
            new Field("Drop", 0, 5, FieldKind.Enum, "What the hive drops when knocked down (common drops match OoT)", Item00Type),
            new Field("Collectible flag", 8, 7, FieldKind.Int, "Flag tracking this drop as collected (0–127)",
                      Flag: FlagKind.Collectible, Role: FlagRole.Both),
        ]),

        // Bg_Ladder (MM 0x163) — the placeable wooden ladder. size = params & 0xFF (rung count → model +
        // its own climbable dynapoly collision); appear switch flag = (params>>8)&0xFF (z_bg_ladder.h).
        [0x0163] = new Def("Ladder (Bg_Ladder)", [
            new Field("Size", 0, 8, FieldKind.Enum, "Rung count / height (also sets the collision height)",
                      new[] { "12 rungs (short)", "16 rungs", "20 rungs", "24 rungs (tall)" }),
            new Field("Appear switch flag", 8, 8, FieldKind.Int,
                      "The ladder starts HIDDEN and non-climbable until this switch flag is set, then it fades in and becomes climbable (z_bg_ladder.c: BgLadder_Wait). Bg_Ladder is inherently switch-gated — an always-present ladder is baked room collision, not this actor.",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
        ], "Self-sufficient: installs its own climbable collision, so Link can climb it with no extra climbable-wall brush."),

        // #4: MM En_Ossan (0x02A) — Curiosity Shop man vs Part-Time worker (params picks the object/model).
        [0x002A] = new Def("Shop NPC (En_Ossan)", [
            new Field("Who", 0, 8, FieldKind.Enum, "Which shop NPC body model", ["Curiosity Shop Man", "Part-Time Worker"]),
        ]),
        // #4: MM En_Sekihi (0x15C) — graves / Song-of-Soaring pedestal; type (params & 0xF) picks the object.
        [0x015C] = new Def("Grave / Pedestal (En_Sekihi)", [
            new Field("Monument", 0, 4, FieldKind.Enum, "Which grave or pedestal model", [
                "Sun's Song Grave (Triforce)", "Sun's Song Grave (Goron)", "Sun's Song Grave (Kokiri)",
                "Song of Soaring Pedestal", "Mikau's Grave",
            ]),
        ]),
        // #4: MM En_Test2 (0x158) — lens-of-truth props; params (0..12) picks the object.
        [0x0158] = new Def("Lens Object (En_Test2)", [
            new Field("Object", 0, 8, FieldKind.Enum, "Which lens-of-truth-revealed object", [
                "Deku Palace pit", "Sichitai", "Yukimura", "Snowhead (3)", "Snowhead (4)", "Snowhead (5)",
                "Glasses cave", "Graveyard (7)", "Graveyard (8)", "Snowhead (9)", "Snowhead (10)",
                "Snowhead (11)", "Snowhead (12)",
            ]),
        ]),

        // En_Box (0x006) — z_en_box.h: ENBOX_GET_TYPE=[12,4], ENBOX_GET_ITEM=[5,7], treasureFlag=[0,5].
        [0x0006] = new Def("Treasure Chest", [
            new Field("Chest type", 12, 4, FieldKind.Enum, "Size, appearance and spawn condition", BoxType),
            new Field("Contents", 5, 7, FieldKind.Enum, "Item given when the chest is opened (None = empty chest)", GetItemTable.MM),
            new Field("Treasure flag", 0, 5, FieldKind.Int, "Marks this chest as opened (0–31)",
                      Flag: FlagKind.Chest, Role: FlagRole.Both),
        ]),

        // Obj_Switch (0x093) — type=[0,3], subtype=[4,3], switchFlag=[8,7].
        [0x0093] = new Def("Switch", [
            new Field("Switch type", 0, 3, FieldKind.Enum, "Floor / eye / crystal switch", MmSwitchType),
            new Field("Behaviour", 4, 3, FieldKind.Enum, "How the switch latches", SwitchSubtype),
            new Field("Switch flag", 8, 7, FieldKind.Int, "Scene switch flag set when activated (0–127)",
                      Flag: FlagKind.Switch, Role: FlagRole.Setter),
        ]),

        // Obj_Lightswitch (0x0B2) — Stone Tower flip / sun switch: type=[4,2], invisible=[3,1], switchFlag=[8,7].
        [0x00B2] = new Def("Sun / Stone-Tower Flip Switch", [
            new Field("Switch type", 4, 2, FieldKind.Enum, "Regular sun switch or Stone Tower inversion flip", LightswitchType),
            new Field("Invisible", 3, 1, FieldKind.Flag, "Hidden until revealed"),
            new Field("Switch flag", 8, 7, FieldKind.Int, "Switch flag set when lit (0–127)",
                      Flag: FlagKind.Switch, Role: FlagRole.Setter),
        ]),

        // Obj_Tsubo (0x082) — drop=[0,5], type=[7,2], collectibleFlag=[9,7].
        [0x0082] = new Def("Pot", [
            new Field("Drop contents", 0, 5, FieldKind.Enum, "What the pot drops when broken", Drop),
            new Field("Pot type", 7, 2, FieldKind.Int, "Appearance / behaviour (0–3)"),
            new Field("Collectible flag", 9, 7, FieldKind.Int, "Flag tracking this drop as collected (0–127)",
                      Flag: FlagKind.Collectible, Role: FlagRole.Both),
        ]),

        // En_Item00 (0x00E) — z_en_item00.c: type=[0,8], flag=(params&0x7F00)>>8, 0x8000 spawn.
        [0x000E] = new Def("Collectible Item", [
            new Field("Item type", 0, 8, FieldKind.Enum, "What this free-standing pickup is (rupee/heart/key…)", Drop),
            new Field("Collectible flag", 8, 7, FieldKind.Int, "Flag tracking this pickup as collected (0–127)",
                      Flag: FlagKind.Collectible, Role: FlagRole.Both),
            new Field("Falling/arcing drop", 15, 1, FieldKind.Flag, "0x8000: spawn as a thrown/falling drop (NOT a respawn condition)"),
        ]),

        // En_Door (0x005) — type=[7,3]; transition-actor index=[10,6].
        [0x0005] = new Def("Door", [
            new Field("Door type", 7, 3, FieldKind.Enum, "Behaviour. NOTE type 0 is 'whole-day' (day/night-CONDITIONAL) and can hit MM's missing-text path — pick a specific type (e.g. a plain/scheduled door) for a normal always-usable door.", DoorType),
            new Field("Transition index", 10, 6, FieldKind.Int, "Index into the scene's transition-actor list (room link)"),
        ], "Doors are transition actors: the transition index ties the two rooms it connects. Type 0 = day/night conditional — not a plain door."),

        // Elf_Msg (0x08B) — MM message/inspect region: textId=(params&0xFF)+0x200; switchFlag=[8,7],
        // tallRegion=[15,1].
        [0x008B] = new Def("Message Region (Elf_Msg)", [
            new Field("Message", 0, 8, FieldKind.Message, "Dialogue shown when entered (textId 0x200 + value)", TextIdBase: 0x200),
            new Field("Switch flag", 8, 7, FieldKind.Int, "Switch flag it reads/responds to (0–127)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
            new Field("Tall region", 15, 1, FieldKind.Flag, "Enlarge the trigger volume"),
        ], "Invisible proximity message/trigger region, gated by a switch flag."),

        // Elf_Msg2 (0x0C6) — same logic shape as Elf_Msg.
        [0x00C6] = new Def("Message Region (Elf_Msg2)", [
            new Field("Message", 0, 8, FieldKind.Message, "Dialogue shown when entered (textId 0x200 + value)", TextIdBase: 0x200),
            new Field("Switch flag", 8, 7, FieldKind.Int, "Switch flag it reads/responds to (0–127)",
                      Flag: FlagKind.Switch, Role: FlagRole.Both),
        ], "Invisible proximity message/trigger region gated by a switch flag."),

        // En_Kanban (MM sign, 0x0A8) — z_en_kanban.c: textId = params | 0x300.
        [0x00A8] = new Def("Sign (En_Kanban)", [
            new Field("Message", 0, 8, FieldKind.Message, "Sign dialogue (textId 0x300 + value)", TextIdBase: 0x300),
        ]),

        // Obj_Wturn (0x027) — Stone Tower inverter: OBJWTURN_GET_SWITCH_FLAG = params.
        [0x0027] = new Def("Stone Tower Inverter (Obj_Wturn)", [
            new Field("Switch flag", 0, 8, FieldKind.Int, "Flag the flip switch sets; a mismatch with the current scene triggers the invert transition",
                      Flag: FlagKind.Switch, Role: FlagRole.Reader),
        ], "Watches the flip-switch flag and warps between the normal and inverted Stone Tower scenes."),
    };

    /// <summary>Schema for an actor: the hand-curated one if present (richer labels + enum options), else
    /// one auto-derived from the decomp param macros (named fields, no raw hex), else null.</summary>
    public static Def? For(bool isOoT, ushort actorId)
    {
        var curated = (isOoT ? OoT : MM).GetValueOrDefault(actorId);
        if (curated != null) return curated;
        return ActorParamSchemaExtractor.For(isOoT).GetValueOrDefault(actorId);
    }

    public static bool Has(bool isOoT, ushort actorId) => For(isOoT, actorId) != null;

    /// <summary>All hand-curated actor schemas for a game (for validation / self-tests).</summary>
    public static IEnumerable<KeyValuePair<ushort, Def>> CuratedDefs(bool isOoT) => isOoT ? OoT : MM;
}
