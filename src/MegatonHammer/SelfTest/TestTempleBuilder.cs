using System.Drawing;
using System.Drawing.Imaging;
using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Rom;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Authors a complete demo dungeon project ("Test Temple") entirely through the editor's own
/// document model — brushwork rooms, transition-actor doors, enemies, chests, pots, rupees, an
/// eye-switch puzzle, a Dark Link miniboss and a Gohma boss — saves it as a .mhproj, renders a
/// top-down map PNG, and round-trip-compiles it. Run: MegatonHammer --testtemple [outDir]
///
/// Forest-Temple themed: Forest Temple textures + music (NA_BGM_FOREST_TEMPLE 0x2C), dungeon
/// lighting, and a walk-in/title area name. All actor ids + params are the verified OoT values.
/// </summary>
public static class TestTempleBuilder
{
    private static readonly string OotRom = Editor.AppPaths.Rom(@"Legend of Zelda, The - Ocarina of Time (USA).z64");
    private static readonly string MmRom  = Editor.AppPaths.Rom(@"Legend of Zelda, The - Majora's Mask (USA).z64");

    // Geometry constants (Zelda world units).
    private const int Cell = 1200;     // room footprint (X and Z)
    private const int Half = 600;      // half footprint
    private const int Wall = 60;       // wall thickness
    private const int Height = 400;    // interior height
    // Door opening sized to the OoT wooden-door frame: ~128u wide x ~224u tall (the gap fits a door
    // model snugly, instead of the old cavernous 260x280 hole). DoorHalf is the half-width.
    private const float DoorHalf = 64f, DoorTop = 224f;

    // Actor ids (verified against oot-master/include/tables/actor_table.h).
    private const ushort A_PLAYER = 0x0000, A_STALFOS = 0x0002, A_DOOR = 0x0009, A_BOX = 0x000A,
        A_POE = 0x000D, A_ITEM00 = 0x0015, A_GOHMA = 0x0028, A_SHUTTER = 0x002E, A_DARKLINK = 0x0033,
        A_SKULLTULA = 0x0037, A_BUBBLE = 0x0069, A_FLOORMAS = 0x008E, A_POT = 0x0111,
        A_SWITCH = 0x012A, A_WOLFOS = 0x01AF;
    // GI item ids (include/z64item.h): Bow 0x04, Compass 0x40, Map 0x41, Small Key 0x42.
    private const int GI_BOW = 0x04, GI_COMPASS = 0x40, GI_MAP = 0x41, GI_KEY = 0x42;

    private sealed class Room
    {
        public int Gx, Gz; public string Name = ""; public ZRoom Z = null!;
        public float Cx => Gx * Cell + Half;
        public float Cz => Gz * Cell + Half;
        public readonly HashSet<char> OpenSides = [];   // 'E','W','N','S' walls that have a doorway
        /// <summary>Floor height. 0 for normal rooms; a deep negative for a boss pit so a ceiling-spawning
        /// boss (Gohma forces world.y = -300) sits ABOVE the floor instead of below it (#7). Doorways stay
        /// at the global walk-in sill (y = 0), so Link enters and drops into the pit.</summary>
        public float FloorY;
    }

    public static void Build(string outDir, bool mm = false, bool scrollFloor = false)
    {
        Directory.CreateDirectory(outDir);
        var (floorTex, wallTex, ceilTex) = ForestTextures(mm);
        Console.WriteLine($"[testtemple] textures: floor={floorTex} wall={wallTex} ceil={ceilTex}");

        var doc = new MapDocument();
        doc.InitGameDefaults(mm);   // set the project's game (MM vs OoT) so playtest uses the right engine
        var scene = doc.Scene;
        scene.Name = "Test Temple";

        // ── Scene settings: Forest Temple theme, dungeon lighting, walk-in title ──
        var st = scene.Settings;
        st.AreaName       = "Test Temple";        // title card + dungeon map screen
        st.StartWeekEvents = [0x4920];            // demo MM starting weekEventReg flag (validates the boot-hook path)
        st.MusicSeq       = 0x2C;                 // NA_BGM_FOREST_TEMPLE
        st.Sky            = SkyMode.None;         // enclosed dungeon
        st.SkyboxId       = 0;
        st.IndoorLighting = true;
        st.Dungeon        = true;                 // load gameplay_dangeon_keep (dungeon pots/keys/doors) (#14/#11)
        st.Ambient   = RgbColor.From(54, 66, 60); st.FogColor = RgbColor.From(30, 40, 38);
        st.Light1Col = RgbColor.From(150, 170, 150); st.Light2Col = RgbColor.From(40, 55, 70);
        // Fog must reach past the room diagonal or the far walls fog to near-black ("culling at distance").
        // The old 0x03FF (≈1023u) fully fogged a 1200u room; push it out to the dungeon-scale default.
        st.FogNear = 0x0BB8; st.FogFar = 0x3200;

        // ── Room layout (grid → an L-shaped path 0→1→2→3→4→5→6) ──
        var rooms = new Room[]
        {
            new() { Gx = 0, Gz = 0, Name = "Entrance Hall" },
            new() { Gx = 1, Gz = 0, Name = "Guardroom" },
            new() { Gx = 2, Gz = 0, Name = "Locked Passage" },
            new() { Gx = 2, Gz = 1, Name = "Bow Vault" },
            new() { Gx = 1, Gz = 1, Name = "Eye Gallery" },
            new() { Gx = 0, Gz = 1, Name = "Mirror Hall" },
            // Sanctum (Gohma). NOTE: a deep boss "pit" (FloorY below Gohma's hard-coded y=-300) renders
            // badly in-engine — the tall negative-Y room produces a hall-of-mirrors + invisible actors. The
            // correct fix is a SEPARATE boss scene reached through the boss door, like vanilla; see
            // docs/boss-rooms-and-linking-plan.md. Until that lands, this stays a normal room (Gohma spawns
            // at her forced y=-300, i.e. below the floor — a known limitation, not the pit regression).
            new() { Gx = 0, Gz = 2, Name = "Sanctum" },
        };
        rooms[0].Z = scene.Rooms[0];
        for (int i = 1; i < rooms.Length; i++) rooms[i].Z = scene.AddRoom();
        for (int i = 0; i < rooms.Length; i++) { rooms[i].Z.Name = rooms[i].Name; rooms[i].Z.Settings.Echo = 0x20; }
        scene.ActiveRoom = rooms[0].Z;

        // ── Doors (transition actors) linking consecutive rooms ──
        // (a, b, doorActorId, params) — geometry openings are derived from the room adjacency.
        var doors = new (int A, int B, ushort Id, ushort Params, string Note)[]
        {
            (0, 1, A_DOOR,    0x0000, "wooden"),                 // ROOMLOAD
            (1, 2, A_SHUTTER, 0x0040, "barred until cleared"),   // SHUTTER_FRONT_CLEAR (type1)
            (2, 3, A_DOOR,    0x0080, "small-key locked"),       // DOOR_LOCKED (type1)
            (3, 4, A_DOOR,    0x0000, "wooden"),
            (4, 5, A_SHUTTER, 0x0085, "opens on eye switch"),    // SHUTTER_FRONT_SWITCH flag 5
            // New#5: a room-clear shutter never opens on Dark Link — En_Torch2 is ACTORCAT_BOSS, and OoT only
            // sets the room-clear flag when the last ACTORCAT_ENEMY dies (z_actor.c Actor_Remove). Single-room
            // miniboss gating needs a controller actor (Water Temple uses En_Blkobj); that belongs to the
            // boss-room-linking work (docs/boss-rooms-and-linking-plan.md). Until then this is a normal door
            // so the player isn't soft-locked after the fight.
            (5, 6, A_DOOR, 0x0000, "wooden (boss gating pending boss-room linking)"),
        };
        foreach (var d in doors) MarkDoorSides(rooms[d.A], rooms[d.B]);

        // ── Build geometry shell for every room ──
        foreach (var r in rooms) BuildShell(r, floorTex, wallTex, ceilTex);

        // ── Place door transition actors (array index == scene room index) ──
        foreach (var d in doors) PlaceDoor(rooms[d.A], d.A, rooms[d.B], d.B, d.Id, d.Params);

        // ── Per-room contents (enemies / chests / pots / rupees / puzzle) ──
        var rng = new Random(0x4D48);   // deterministic "random" scatter
        Populate(rooms, rng);

        // ── A demo path (the 0x0D track a moving platform / patrol would follow) ──
        var eye = rooms[4];
        scene.Paths.Add(new ZPath(new[]
        {
            new Vector3(eye.Cx - 320, 60, eye.Cz - 320),
            new Vector3(eye.Cx + 320, 60, eye.Cz - 320),
            new Vector3(eye.Cx + 320, 60, eye.Cz + 320),
            new Vector3(eye.Cx - 320, 60, eye.Cz + 320),
        }) { Name = "Platform Loop" });

        // ── Link spawn in the entrance, facing the first door (+X) ──
        st.SpawnPos  = new Vector3(rooms[0].Cx, 10, rooms[0].Cz);
        st.SpawnRoom = 0;
        st.SpawnYaw  = unchecked((short)0x4000);   // face +X toward Guardroom

        // Brush-authored animated texture test: make the floor texture scroll (validates the editor preview
        // + MM AnimatedMaterial export). At V=0.5 tiles/sec the floor visibly flows toward +V in-game (MM).
        if (scrollFloor)
        {
            st.TextureScrolls.Add(new TextureScroll(floorTex, 0f, 0.5f));
            Console.WriteLine($"[testtemple] authored floor scroll on '{floorTex}' (V=0.5/sec)");
        }

        // ── Save .mhproj ──
        string proj = Path.Combine(outDir, "Test_Temple.mhproj");
        ProjectSerializer.Save(doc, proj);

        // ── Render the dungeon map + an isometric overview ──
        string map = Path.Combine(outDir, "Test_Temple_map.png");
        RenderMap(rooms, doors, scene.Paths, map);
        string iso = Path.Combine(outDir, "Test_Temple_iso.png");
        RenderIso(rooms, iso);

        // ── Verify it compiles (round-trip through the exporter) ──
        var (sd, rd) = SceneExporter.BuildBinaries(scene);
        int brushes = scene.Rooms.Sum(r => r.Geometry.Count);
        int actors  = scene.Rooms.Sum(r => r.Actors.Count(a => !a.IsTransition));
        int trans   = scene.Rooms.Sum(r => r.Actors.Count(a => a.IsTransition));
        Console.WriteLine($"[testtemple] saved {proj}");
        Console.WriteLine($"[testtemple] map   {map}");
        Console.WriteLine($"[testtemple] {rooms.Length} rooms, {brushes} brushes, {actors} actors, {trans} doors");
        Console.WriteLine($"[testtemple] compile OK: scene={sd.Length}b, {rd.Count} room binaries ({rd.Sum(r => r.Length)}b)");
    }

    // ── Geometry ──────────────────────────────────────────────────────────

    private static void MarkDoorSides(Room a, Room b)
    {
        if (b.Gx == a.Gx + 1) { a.OpenSides.Add('E'); b.OpenSides.Add('W'); }
        else if (b.Gx == a.Gx - 1) { a.OpenSides.Add('W'); b.OpenSides.Add('E'); }
        else if (b.Gz == a.Gz + 1) { a.OpenSides.Add('S'); b.OpenSides.Add('N'); }
        else if (b.Gz == a.Gz - 1) { a.OpenSides.Add('N'); b.OpenSides.Add('S'); }
    }

    private const float DoorSill = 0f;   // doorways sit at the global walk-in level (a normal room's floor)

    // ── MM day + time playtest demo ─────────────────────────────────────────
    // An outdoor MM scene that makes the day/time boot-state VISIBLE and identical on 2Ship and vanilla-MM
    // (PJ64): three pots, each gated (half-day spawn bits) to a different day — only the booted day's pot
    // appears — under an open sky whose lighting follows the booted time. Set the scene's "Playtest day" +
    // "Playtest time" and playtest: day 1/2/3 shows a different pot; day vs night changes the sky/lighting.
    public static void BuildMmDayTimeDemo(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var (floorTex, wallTex, _) = ForestTextures(mm: true);
        var doc = new MapDocument();
        doc.InitGameDefaults(true);
        var scene = doc.Scene;
        scene.Name = "MM Day-Time Demo";
        var st = scene.Settings;
        st.AreaName          = "Day-Time Demo";
        st.Sky               = SkyMode.Day;   // outdoor: sky + ambient lighting follow save.time (day↔night)
        st.SkyboxId          = 1;
        st.IndoorLighting    = false;
        st.MusicSeq          = 0x0A;
        st.PlaytestDay       = 1;             // boots day 1 by default; change to 2/3 to see a different pot
        st.PlaytestTimeOfDay = 0x8000;        // noon; set ~0x0000 for a night boot

        var room = scene.Rooms[0];
        AddBox(room, (-460, -30, -460), (460, 0, 460), floorTex);                 // floor
        AddBox(room, (-460, 0, -480), (460, 140, -460), wallTex);                 // low border walls (no ceiling → sky visible)
        AddBox(room, (-460, 0, 460), (460, 140, 480), wallTex);
        AddBox(room, (-480, 0, -460), (-460, 140, 460), wallTex);
        AddBox(room, (460, 0, -460), (480, 140, 460), wallTex);

        // Day-gated pots (Obj_Tsubo 0x0082). halfDaysBits = ((rotX&7)<<7)|(rotZ&0x7F); IdFlags 0x6000 marks
        // rotX+rotZ as a raw mask (not a facing angle) — honoured by both the OTR (2Ship) and N64 (PJ64) export.
        // DAY1=0xC0 (dawn|night), DAY2=0x30, DAY3=0x0C — see z64actor.h HALFDAYBIT_*.
        (float X, int Hdb)[] pots = { (-220, 0xC0), (0, 0x30), (220, 0x0C) };
        foreach (var (x, hdb) in pots)
            room.Actors.Add(new ZActor
            {
                Number = 0x0082, Variable = 0, XPos = x, YPos = 6, ZPos = -140,
                XRot = (short)((hdb >> 7) & 7), ZRot = (short)(hdb & 0x7F), IdFlags = 0x6000,
            });
        // Always-present reference pot (halfDaysBits 0 = every day) so you can confirm the scene loaded.
        room.Actors.Add(new ZActor { Number = 0x0082, XPos = 0, YPos = 6, ZPos = 160 });

        st.SpawnPos  = new Vector3(0, 8, 300);
        st.SpawnRoom = 0;
        st.SpawnYaw  = unchecked((short)0x8000);   // face -Z toward the pots

        string proj = Path.Combine(outDir, "MM_DayTime_Demo.mhproj");
        ProjectSerializer.Save(doc, proj);
        Console.WriteLine($"[mmdaytimedemo] wrote {proj}");
        var (sd, rd) = SceneExporter.BuildBinaries(scene);
        Console.WriteLine($"[mmdaytimedemo] export OK: scene {sd.Length}B, {rd.Count} room(s). " +
                          "Playtest with Playtest day 1/2/3 (different pot each) + day/night time (sky changes) on 2Ship AND MM PJ64.");
    }

    // ── OoT time+age setups + custom dialogue-tree demo ─────────────────────
    // One OoT level with the four age×time SETUPS (Child/Adult × Day/Night), each showing different objects and
    // a custom dialogue-tree NPC (En_MhTalk) that demonstrates Yes/No prompts, item-give, and rupee purchases —
    // with rupees placed nearby to afford them. Grass floor + concrete (Stone) walls. Playtest the four states
    // from the Playtest dialog: pick Child/Adult + Day/Night and the game loads the matching setup.
    // One dialogue line for a setup: prompt text, the two choice labels, the GetItem id handed out (-1 = none),
    // and the rupee cost of that give (0 = free gift → a plain Message; >0 = a Yes/No purchase Prompt).
    private readonly record struct DemoLine(string Text, string Yes, string No, int Give, int Cost);

    // A themed OoT demo level: floor/wall textures, room half-extent, and the four age×time dialogue trees.
    private readonly record struct OotDemoVariant(string File, string Name, string Floor, string Wall, int Half,
        DemoLine ChildDay, DemoLine ChildNight, DemoLine AdultDay, DemoLine AdultNight);

    public static void BuildOotSetupDemo(string outDir)
    {
        Directory.CreateDirectory(outDir);
        // A few themed variants so there's real variation to compare: different textures, room size, objects,
        // NPC banter, items given, and rupee prices — while each still exercises all four age×time setups plus
        // the Yes/No + item-give + rupee-purchase dialogue paths.
        var variants = new[]
        {
            new OotDemoVariant("OoT_Demo_Meadow", "Meadow (Time+Age Demo)", "Grass", "Stone", 500,
                new DemoLine("A young Kokiri grins in the sun.&Want a Deku Nut?", "Sure!", "No thanks", 0x02, 0),
                new DemoLine("A merchant whispers in the dark.&Deku Stick - 5 Rupees?", "Buy", "Leave", 0x61, 5),
                new DemoLine("Talon tips his hat.&Piece of Heart for 20 Rupees?", "Buy", "No", 0x3E, 20),
                new DemoLine("The princess speaks softly.&Take this for your journey.", "", "", 0x0F, 0)),

            new OotDemoVariant("OoT_Demo_Bazaar", "Bazaar (Time+Age Demo)", "Sand", "Brick", 420,
                new DemoLine("A cheery vendor waves.&Free Recovery Heart - want it?", "Yes!", "Later", 0x48, 0),
                new DemoLine("A hooded seller leans close.&Bottle of Milk for 10 Rupees?", "Buy", "Pass", 0x14, 10),
                new DemoLine("The gerudo shopkeep smirks.&Piece of Heart - 30 Rupees.", "Buy", "No", 0x3E, 30),
                new DemoLine("A quiet trader nods.&Here, take some Deku Nuts.", "", "", 0x02, 0)),

            new OotDemoVariant("OoT_Demo_Quarry", "Quarry (Time+Age Demo)", "Dirt", "Cobble", 560,
                new DemoLine("A dusty miner grins.&Have an Empty Bottle, kid.", "", "", 0x0F, 0),
                new DemoLine("A lantern-lit dealer beckons.&Deku Sticks for 15 Rupees?", "Buy", "Leave", 0x61, 15),
                new DemoLine("The foreman wipes his brow.&Recovery Heart, 5 Rupees?", "Buy", "No", 0x48, 5),
                new DemoLine("An old woman smiles warmly.&Take these Deku Nuts, dear.", "", "", 0x02, 0)),

            new OotDemoVariant("OoT_Demo_Grove", "Grove (Time+Age Demo)", "Grass", "Plank", 460,
                new DemoLine("A forest child offers a drink.&Here - Bottle of Milk!", "", "", 0x14, 0),
                new DemoLine("A shy fairy hovers.&A Piece of Heart, for you.", "", "", 0x3E, 0),
                new DemoLine("A ranch hand counts coins.&Empty Bottle for 20 Rupees?", "Buy", "No", 0x0F, 20),
                new DemoLine("A traveler by the fire nods.&Some Deku Sticks for the road.", "", "", 0x61, 0)),
        };

        foreach (var v in variants)
        {
            var doc = new MapDocument();
            doc.InitGameDefaults(false);   // OoT
            var scene = doc.Scene;
            scene.Name = v.Name;
            var st = scene.Settings;
            st.AreaName       = v.Name;
            // Enclosed room with STATIC per-setup lighting (like the working handmade maps). Outdoor lighting
            // (IndoorLighting=false) drives the time-of-day env system, which needs a populated Environments
            // list — with none, the game reads garbage and flickers red/black over everything. Indoor + no
            // skybox is stable; each setup carries its own day/night light values (set below), so Day setups
            // look bright and Night setups look dark without any of that flicker.
            st.Sky            = SkyMode.None;
            st.SkyboxId       = 0;
            st.IndoorLighting = true;

            // Shared geometry: a fully enclosed room (floor, four walls, ceiling) so there's no open sky to
            // read as void once the skybox is off.
            var room = scene.Rooms[0];
            int H = v.Half;
            AddBox(room, (-H, -30, -H), (H, 0, H), v.Floor);            // floor
            AddBox(room, (-H, 0, -H - 20), (H, 220, -H), v.Wall);       // walls
            AddBox(room, (-H, 0, H), (H, 220, H + 20), v.Wall);
            AddBox(room, (-H - 20, 0, -H), (-H, 220, H), v.Wall);
            AddBox(room, (H, 0, -H), (H + 20, 220, H), v.Wall);
            AddBox(room, (-H, 220, -H), (H, 240, H), v.Wall);           // ceiling
            st.SpawnPos = new Vector3(0, 8, H - 40); st.SpawnRoom = 0; st.SpawnYaw = unchecked((short)0x8000);

            // Dialogue trees (scene-wide message bank), one per setup NPC — textId 0x1000 + slot. On SoH/2Ship the
            // generic interpreter honours give/charge/branch; on carts give/charge only apply to native NPCs.
            static MhMessage Msg(int slot, DemoLine d)
            {
                bool prompt = d.Cost > 0;   // a purchase is a Yes/No; a free gift is a plain one-line message
                var m = new MhMessage { Id = 0x1000 + slot, Kind = prompt ? MhMsgKind.Prompt : MhMsgKind.Message,
                    Text = d.Text, Choice1 = d.Yes, Choice2 = d.No, IsOverride = true };
                m.Outcome1 = new MhOutcome { GiveItem = d.Give, ChargeRupees = d.Cost > 0, RupeeCost = d.Cost };
                return m;
            }
            var lines = new[] { v.ChildDay, v.ChildNight, v.AdultDay, v.AdultNight };
            for (int i = 0; i < 4; i++) scene.Messages.Add(Msg(i, lines[i]));

            // Static day/night lighting per setup: Day bright & warm, Night dark & blue. Written into each setup's
            // own scene header (SceneExporter emits per-setup env light settings), so switching setup switches look.
            static void SetLight(SceneSettings s, bool night)
            {
                if (night)
                {
                    s.Ambient   = RgbColor.From(30, 34, 54);  s.Light1Col = RgbColor.From(70, 82, 120);
                    s.Light2Col = RgbColor.From(22, 26, 46);  s.FogColor  = RgbColor.From(12, 16, 38);
                    s.FogNear   = 900; s.FogFar = 9000;
                }
                else
                {
                    s.Ambient   = RgbColor.From(108, 104, 92); s.Light1Col = RgbColor.From(255, 250, 235);
                    s.Light2Col = RgbColor.From(120, 122, 140); s.FogColor = RgbColor.From(198, 214, 235);
                    s.FogNear   = 980; s.FogFar = 12800;
                }
            }

            // Per-setup contents. En_MhTalk (id 0x01D7 on SoH) is now a VISIBLE, talkable dialogue point built
            // into the forks — talking to it runs the authored tree (textId 0x1000+slot) and the interpreter
            // applies its outcomes (give / charge / branch). Plus two decorative objects (pots on even setups,
            // chests on odd), and — for any setup whose NPC charges rupees — 3 red rupees (60r) to grab first.
            var kinds = new[] { (SetupLayer.ChildDay, "Child (Day)"), (SetupLayer.ChildNight, "Child (Night)"),
                                (SetupLayer.AdultDay, "Adult (Day)"), (SetupLayer.AdultNight, "Adult (Night)") };
            scene.Setups.Clear();
            for (int i = 0; i < 4; i++)
            {
                bool night = (i % 2) == 1;   // ChildNight / AdultNight
                var a = new List<ZActor>();
                a.Add(new ZActor { Number = Editor.ActorDatabase.MhTalkIdOot, Variable = (ushort)i, XPos = 0, YPos = 20, ZPos = 0, YRot = unchecked((short)0x8000) }); // visible dialogue NPC, slot i
                ushort decor = (i % 2 == 0) ? (ushort)0x0111 : (ushort)0x000A;   // pot / chest
                a.Add(new ZActor { Number = decor, Variable = (ushort)(decor == 0x000A ? 0x1000 : 0), XPos = -180, YPos = decor == 0x000A ? 0 : 6, ZPos = -200 });
                a.Add(new ZActor { Number = decor, Variable = (ushort)(decor == 0x000A ? 0x1000 : 0), XPos =  180, YPos = decor == 0x000A ? 0 : 6, ZPos = -200 });
                if (lines[i].Cost > 0)
                    for (int r = 0; r < 3; r++) a.Add(new ZActor { Number = 0x0015, Variable = 2, XPos = -120 + r * 120, YPos = 10, ZPos = H - 120 }); // red rupees
                var ss = st.Clone(); SetLight(ss, night);
                scene.Setups.Add(new ZSetup { Name = kinds[i].Item2, Layer = kinds[i].Item1, Settings = ss, Environments = [.. scene.Environments], RoomActors = [a] });
            }
            scene.ActiveSetup = 0;
            scene.LoadSetup(0);

            string proj = Path.Combine(outDir, v.File + ".mhproj");
            ProjectSerializer.Save(doc, proj);
            var (sd, rd) = SceneExporter.BuildBinaries(scene);
            Console.WriteLine($"[ootsetupdemo] wrote {proj} — scene {sd.Length}B, {rd.Count} room, {scene.Setups.Count} setups, {scene.Messages.Count} dialogue trees ({v.Floor}/{v.Wall}, half={H}).");
        }
        Console.WriteLine($"[ootsetupdemo] {variants.Length} variants written to {outDir}. Playtest each: pick Child/Adult + Day/Night → matching setup; talk to the NPC for Yes/No + give + rupee purchase.");
    }

    /// <summary>A tiny OoT level with a single chest whose contents are the SoH-exclusive Fierce Deity's Mask
    /// (via the actor "SoH item" dropdown → mh/chests → the fork's chest-give hook). Open the chest and the mask
    /// becomes owned, reachable from the pause menu on the first bottle slot like Roc's Feather.</summary>
    public static void BuildFdMaskChestDemo(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var doc = new MapDocument();
        doc.InitGameDefaults(false);   // OoT
        var scene = doc.Scene;
        scene.Name = "OoT_Demo_FdMaskChest";
        var st = scene.Settings;
        st.AreaName = "FD Mask Chest";
        // Stable enclosed-room lighting (see BuildOotSetupDemo): indoor + no skybox, static daytime light.
        st.Sky = SkyMode.None; st.SkyboxId = 0; st.IndoorLighting = true;
        st.Ambient   = RgbColor.From(108, 104, 92);  st.Light1Col = RgbColor.From(255, 250, 235);
        st.Light2Col = RgbColor.From(120, 122, 140);  st.FogColor  = RgbColor.From(198, 214, 235);
        st.FogNear = 980; st.FogFar = 12800;

        const int H = 360;
        var room = scene.Rooms[0];
        AddBox(room, (-H, -30, -H), (H, 0, H), "Grass");             // floor
        AddBox(room, (-H, 0, -H - 20), (H, 220, -H), "Stone");       // walls
        AddBox(room, (-H, 0, H), (H, 220, H + 20), "Stone");
        AddBox(room, (-H - 20, 0, -H), (-H, 220, H), "Stone");
        AddBox(room, (H, 0, -H), (H + 20, 220, H), "Stone");
        AddBox(room, (-H, 220, -H), (H, 240, H), "Stone");           // ceiling
        st.SpawnPos = new Vector3(0, 8, H - 60); st.SpawnRoom = 0; st.SpawnYaw = unchecked((short)0x8000);

        // The chest: En_Box (0x000A), treasure flag 1 (Variable bits 0-4), native contents = Recovery Heart
        // (get-item 0x48, bits 5-11 → (0x48<<5)|1 = 0x0901) as a valid placeholder so the chest doesn't crush
        // Link on an empty give. The "SoH item" = fd_mask is carried out-of-band in mh/chests; opening the chest
        // (setting treasure flag 1) makes the fork give the mask right after the placeholder get.
        room.Actors.Add(new ZActor { Number = 0x000A, Variable = 0x0901, MhCustomItem = "fd_mask",
                                     XPos = 0, YPos = 0, ZPos = -120 });
        // A talkable sign-post NPC explaining the test (authored one-line message, textId 0x1000).
        room.Actors.Add(new ZActor { Number = Editor.ActorDatabase.MhTalkIdOot, Variable = 0,
                                     XPos = 140, YPos = 20, ZPos = -120, YRot = unchecked((short)0x8000) });
        scene.Messages.Add(new MhMessage { Id = 0x1000, Kind = MhMsgKind.Message, IsOverride = true,
            Text = "Open the chest for the&Fierce Deity's Mask.&Equip it from the pause menu." });
        // No alt-setups: actors live directly in the room, exported as the single primary scene header.

        string proj = Path.Combine(outDir, "OoT_Demo_FdMaskChest.mhproj");
        ProjectSerializer.Save(doc, proj);
        var (sd, rd) = SceneExporter.BuildBinaries(scene);
        Console.WriteLine($"[fdmaskchestdemo] wrote {proj} — scene {sd.Length}B, {rd.Count} room. " +
                          "Enable SoH-exclusive items + FD Mask in Options, playtest on SoH, open the chest.");
    }

    private static void BuildShell(Room r, string floor, string wall, string ceil)
    {
        float cx = r.Cx, cz = r.Cz, fy = r.FloorY;
        // floor + ceiling slabs (floor at this room's FloorY; ceiling at the shared Height)
        AddBox(r.Z, (cx - Half, fy - 30, cz - Half), (cx + Half, fy, cz + Half), floor);
        AddBox(r.Z, (cx - Half, Height, cz - Half), (cx + Half, Height + 30, cz + Half), ceil);
        // four walls, with a centred doorway on any open side
        AddWallAlongZ(r.Z, cx - Half, cx - Half + Wall, cz - Half, cz + Half, r.OpenSides.Contains('W'), cz, wall, fy); // West
        AddWallAlongZ(r.Z, cx + Half - Wall, cx + Half, cz - Half, cz + Half, r.OpenSides.Contains('E'), cz, wall, fy); // East
        AddWallAlongX(r.Z, cz - Half, cz - Half + Wall, cx - Half, cx + Half, r.OpenSides.Contains('N'), cx, wall, fy); // North
        AddWallAlongX(r.Z, cz + Half - Wall, cz + Half, cx - Half, cx + Half, r.OpenSides.Contains('S'), cx, wall, fy); // South
    }

    // Wall spanning Z (a ±X wall): optional door gap centred at doorC. The door opening is [DoorSill,
    // DoorSill+DoorTop]; a pit room (fy < DoorSill) fills the wall below the sill so it stays solid.
    private static void AddWallAlongZ(ZRoom room, float x0, float x1, float zMin, float zMax, bool door, float doorC, string tex, float fy)
    {
        if (!door) { AddBox(room, (x0, fy, zMin), (x1, Height, zMax), tex); return; }
        AddBox(room, (x0, fy, zMin), (x1, Height, doorC - DoorHalf), tex);
        AddBox(room, (x0, fy, doorC + DoorHalf), (x1, Height, zMax), tex);
        if (fy < DoorSill) AddBox(room, (x0, fy, doorC - DoorHalf), (x1, DoorSill, doorC + DoorHalf), tex);   // below-sill fill
        AddBox(room, (x0, DoorSill + DoorTop, doorC - DoorHalf), (x1, Height, doorC + DoorHalf), tex);       // lintel
    }

    // Wall spanning X (a ±Z wall): optional door gap centred at doorC.
    private static void AddWallAlongX(ZRoom room, float z0, float z1, float xMin, float xMax, bool door, float doorC, string tex, float fy)
    {
        if (!door) { AddBox(room, (xMin, fy, z0), (xMax, Height, z1), tex); return; }
        AddBox(room, (xMin, fy, z0), (doorC - DoorHalf, Height, z1), tex);
        AddBox(room, (doorC + DoorHalf, fy, z0), (xMax, Height, z1), tex);
        if (fy < DoorSill) AddBox(room, (doorC - DoorHalf, fy, z0), (doorC + DoorHalf, DoorSill, z1), tex);   // below-sill fill
        AddBox(room, (doorC - DoorHalf, DoorSill + DoorTop, z0), (doorC + DoorHalf, Height, z1), tex);       // lintel
    }

    private static void AddBox(ZRoom room, (float x, float y, float z) lo, (float x, float y, float z) hi, string tex)
    {
        var s = Solid.CreateBox(new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, hi.y, hi.z));
        foreach (var f in s.Faces) f.TextureName = tex;
        room.Geometry.Add(s);
    }

    // ── Doors + actors ──────────────────────────────────────────────────────

    private static void PlaceDoor(Room a, int ai, Room b, int bi, ushort id, ushort prm)
    {
        // Doorway midpoint on the shared wall; the transition bridges scene rooms ai↔bi. The door is the
        // FRONT side (sides[0]=ai) and its yaw must point FROM the front room (a) TOWARD the back room (b):
        // OoT's func_800C0D34 has Link in the front room walk along the door's rot.y to pass through, and
        // En_Door draws the keyhole/lock on the rot.y-derived side. Earlier this hard-coded +X / +Z, so any
        // door whose neighbour sat to the west or south loaded the wrong way and showed its lock reversed
        // (#15/#17). Pick the yaw by the SIGN of the grid delta. OoT angles: +Z=0x0000, +X=0x4000, -Z=0x8000, -X=0xC000.
        float x, z; short yaw;
        if (b.Gx != a.Gx)
        {
            x = (a.Gx + b.Gx) * Cell / 2f + Half; z = a.Cz;
            yaw = b.Gx > a.Gx ? unchecked((short)0x4000) : unchecked((short)0xC000);   // a→b along ±X
        }
        else
        {
            x = a.Cx; z = (a.Gz + b.Gz) * Cell / 2f + Half;
            yaw = b.Gz > a.Gz ? (short)0x0000 : unchecked((short)0x8000);              // a→b along ±Z
        }
        a.Z.Actors.Add(new ZActor
        {
            Number = id, Variable = prm, XPos = x, YPos = 0, ZPos = z, YRot = yaw,
            IsTransition = true, FrontRoom = (byte)ai, BackRoom = (byte)bi,
            FrontEffect = 0, BackEffect = 0, DisplayName = "⇄ Door",
        });
    }

    private static void Populate(Room[] rooms, Random rng)
    {
        // Room 0 — Entrance: dungeon map chest + ambience.
        Chest(rooms[0], -300, 300, type: 5, gi: GI_MAP, flag: 2);
        Scatter(rooms[0], rng, pots: 3, rupees: 3);

        // Room 1 — Guardroom: enemies; barred door opens on clear; small-key chest.
        // #12: Stalfos params LOW byte is the type; 0 = STALFOS_TYPE_INVISIBLE, which sets ACTOR_FLAG_7
        // (only drawn with the Lens of Truth). Use type 1 (a normal, always-visible grounded Stalfos).
        Enemy(rooms[1], A_STALFOS, 0x0001, -200, -200);
        Enemy(rooms[1], A_BUBBLE,  0xFFFF,  250, -150);
        Enemy(rooms[1], A_BUBBLE,  0xFFFF, -250,  200);
        Enemy(rooms[1], A_SKULLTULA, 0x0000, 200, 250);
        Chest(rooms[1], 0, 0, type: 5, gi: GI_KEY, flag: 0);
        Scatter(rooms[1], rng, pots: 4, rupees: 2);

        // Room 2 — Locked Passage: guarded; the door onward (2→3) is the small-key lock.
        Enemy(rooms[2], A_FLOORMAS, 0x0000, 0, 0);
        Scatter(rooms[2], rng, pots: 2, rupees: 2);

        // Room 3 — Bow Vault: big chest with the Fairy Bow.
        Chest(rooms[3], 0, 0, type: 0, gi: GI_BOW, flag: 1);
        Scatter(rooms[3], rng, pots: 3, rupees: 4);

        // Room 4 — Eye Gallery: arrow eye-switch puzzle; flag 5 opens the door to room 5.
        EyeSwitch(rooms[4], -300, -300, flag: 5);
        EyeSwitch(rooms[4],    0, -350, flag: 6);
        EyeSwitch(rooms[4],  300, -300, flag: 7);
        // #16: En_Wf params = (switchFlag<<8)|type; type 0 = grey WOLFOS_NORMAL, 1 = WOLFOS_WHITE. With
        // switchFlag 0 it ties to switch flag 0 (the room-1 key chest) and is killed once that's set. Use a
        // White Wolfos with switchFlag 0xFF (no switch dependency) so it always spawns. (Eye switches need
        // OBJECT_GAMEPLAY_DANGEON_KEEP — they only render/work now the scene is flagged Dungeon.)
        Enemy(rooms[4], A_WOLFOS, 0xFF01, 0, 200);
        // New#3: gate the compass chest on clearing the room (the Wolfos) — ENBOX_TYPE_ROOM_CLEAR_SMALL (7)
        // appears when the room's enemies are dead, instead of sitting there from the start.
        Chest(rooms[4], 300, 300, type: 7, gi: GI_COMPASS, flag: 3);
        Scatter(rooms[4], rng, pots: 2, rupees: 3);

        // Room 5 — Mirror Hall: Dark Link miniboss; clearing it opens the boss door.
        // #18: En_Torch2 rests at sSpawnPoint.y (its spawn Y) when near home, so any lift leaves him hovering;
        // spawn him exactly on the floor (y:0).
        Enemy(rooms[5], A_DARKLINK, 0x0000, 0, 0, y: 0);
        Scatter(rooms[5], rng, pots: 2, rupees: 1);

        // Room 6 — Sanctum: Gohma.
        Enemy(rooms[6], A_GOHMA, 0x0000, 0, -150);

        // Demonstrate the MM NPC-schedule convention: the room-1 Stalfos stands at two spots by time of
        // day (emitted to mh/schedules; applied by the 2Ship fork). Harmless for OoT (schedules are MM-only).
        var sched = rooms[1].Z.Actors.FirstOrDefault(a => a.Number == A_STALFOS);
        if (sched != null)
            sched.Schedule =
            [
                new Editor.ScheduleRule { Day = 0, StartHour = 6,  StartMin = 0, EndHour = 17, EndMin = 59,
                    X = sched.XPos + 150, Y = sched.YPos, Z = sched.ZPos, Yaw = 0x4000 },     // daytime: east
                new Editor.ScheduleRule { Day = 0, StartHour = 18, StartMin = 0, EndHour = 5,  EndMin = 59,
                    X = sched.XPos - 150, Y = sched.YPos, Z = sched.ZPos, Yaw = unchecked((short)0xC000) }, // night: west
            ];
    }

    // y defaults to a small lift so gravity-driven enemies settle onto the floor; pass y:0 for actors that
    // rest at their spawn Y (En_Torch2/Dark Link mirrors Link from sSpawnPoint.y, so a lift makes him hover — #18).
    private static void Enemy(Room r, ushort id, ushort prm, float dx, float dz, float y = 10) =>
        r.Z.Actors.Add(new ZActor { Number = id, Variable = prm, XPos = r.Cx + dx, YPos = y, ZPos = r.Cz + dz,
            DisplayName = "Enemy" });

    private static void Chest(Room r, float dx, float dz, int type, int gi, int flag) =>
        r.Z.Actors.Add(new ZActor
        {
            Number = A_BOX, Variable = (ushort)((type << 12) | (gi << 5) | (flag & 0x1F)),
            XPos = r.Cx + dx, YPos = 0, ZPos = r.Cz + dz, DisplayName = "Chest"
        });

    private static void EyeSwitch(Room r, float dx, float dz, int flag) =>
        r.Z.Actors.Add(new ZActor
        {
            Number = A_SWITCH, Variable = (ushort)((flag << 8) | 0x02),   // type 2 = eye, set switch flag
            XPos = r.Cx + dx, YPos = 120, ZPos = r.Cz + dz, DisplayName = "Eye Switch"
        });

    private static void Scatter(Room r, Random rng, int pots, int rupees)
    {
        for (int i = 0; i < pots; i++)
        {
            float dx = rng.Next(-380, 380), dz = rng.Next(-380, 380);
            int drop = new[] { 0x00, 0x01, 0x02, 0x03 }[rng.Next(4)];   // green/blue/red rupee or heart
            r.Z.Actors.Add(new ZActor { Number = A_POT, Variable = (ushort)drop,
                XPos = r.Cx + dx, YPos = 0, ZPos = r.Cz + dz, DisplayName = "Pot" });
        }
        for (int i = 0; i < rupees; i++)
        {
            float dx = rng.Next(-380, 380), dz = rng.Next(-380, 380);
            int type = new[] { 0x00, 0x01, 0x02 }[rng.Next(3)];        // green/blue/red rupee
            r.Z.Actors.Add(new ZActor { Number = A_ITEM00, Variable = (ushort)type,
                XPos = r.Cx + dx, YPos = 30, ZPos = r.Cz + dz, DisplayName = "Rupee" });
        }
    }

    // ── Dungeon wall/floor/ceiling textures from the target game's ROM scan ───
    // OoT picks Forest Temple textures; MM has no equivalent folder, so it space-samples the indexed
    // set. Either way the names are rom_{file}_{offset} keyed to that game's ROM, so the matching
    // BuildRomTexResolver(mm) resolves them (an OoT-named texture would miss in the MM index → untextured).
    internal static (string floor, string wall, string ceil) ForestTextures(bool mm)
    {
        // OoT: use the hand-picked Forest-Temple set the user chose in the editor (the auto-sampled ones
        // below were garish). These resolve against the OoT ROM scan the same way the editor renders them.
        if (!mm) return ("rom_1131_007548", "Bmori1_room_14Tex_003560", "rom_1122_00D0D0");
        try
        {
            var rom = new RomImage(mm ? MmRom : OotRom);
            using var src = new RomTextureSource(rom);
            var map = RomAssetIndex.BuildMap(rom);
            var (all, folders) = SceneTextureMapper.Build(rom, src.Scan(), map);
            var names = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                bool pick = mm || folders[i].Any(f => f.Contains("Forest Temple", StringComparison.OrdinalIgnoreCase));
                if (pick) names.Add($"rom_{all[i].FileIndex:D4}_{all[i].Offset:X6}");
            }
            if (names.Count >= 3)
                return (names[names.Count / 4], names[names.Count / 2], names[^1]);
        }
        catch { }
        return ("Stone", "Brick", "Wood");   // procedural fallback
    }

    // ── Top-down dungeon map render ───────────────────────────────────────
    private static void RenderMap(Room[] rooms, (int A, int B, ushort Id, ushort Params, string Note)[] doors, List<ZPath> paths, string path)
    {
        int gxMax = rooms.Max(r => r.Gx), gzMax = rooms.Max(r => r.Gz);
        const int Tile = 200, Pad = 90;
        int w = (gxMax + 1) * Tile + Pad * 2, h = (gzMax + 1) * Tile + Pad * 2 + 120;
        using var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(18, 22, 20));
        using var title = new Font("Segoe UI", 16, FontStyle.Bold);
        using var roomFont = new Font("Segoe UI", 9, FontStyle.Bold);
        using var small = new Font("Segoe UI", 7);
        g.DrawString("TEST TEMPLE — map", title, Brushes.Gainsboro, Pad, 18);

        Point Center(Room r) => new(Pad + r.Gx * Tile + Tile / 2, Pad + 60 + r.Gz * Tile + Tile / 2);

        // doors (lines between room centres) drawn under the rooms
        using var doorPen = new Pen(Color.FromArgb(120, 110, 90), 6);
        foreach (var d in doors) g.DrawLine(doorPen, Center(rooms[d.A]), Center(rooms[d.B]));

        foreach (var r in rooms)
        {
            int x = Pad + r.Gx * Tile, y = Pad + 60 + r.Gz * Tile;
            var rect = new Rectangle(x + 14, y + 14, Tile - 28, Tile - 28);
            g.FillRectangle(new SolidBrush(Color.FromArgb(46, 58, 50)), rect);
            g.DrawRectangle(new Pen(Color.FromArgb(150, 165, 150), 2), rect);
            g.DrawString(r.Name, roomFont, Brushes.Gainsboro, x + 18, y + 18);

            // actor markers
            int n = rooms.ToList().IndexOf(r);
            foreach (var a in r.Z.Actors)
            {
                var (col, label) = MarkerFor(a);
                if (col == Color.Empty) continue;
                float fx = (a.XPos - (r.Gx * Cell)) / Cell;   // 0..1 within the cell
                float fz = (a.ZPos - (r.Gz * Cell)) / Cell;
                int px = (int)(x + 24 + fx * (Tile - 48));
                int py = (int)(y + 30 + fz * (Tile - 60));
                g.FillEllipse(new SolidBrush(col), px - 5, py - 5, 10, 10);
                g.DrawEllipse(Pens.Black, px - 5, py - 5, 10, 10);
            }
        }

        // paths (0x0D tracks) — orange polylines with waypoint dots
        PointF WorldToMap(float wx, float wz)
        {
            int gx = (int)(wx / Cell), gz = (int)(wz / Cell);
            float fx = (wx - gx * Cell) / Cell, fz = (wz - gz * Cell) / Cell;
            return new PointF(Pad + gx * Tile + 24 + fx * (Tile - 48), Pad + 60 + gz * Tile + 30 + fz * (Tile - 60));
        }
        using (var pp = new Pen(Color.Orange, 2.5f))
            foreach (var pth in paths)
            {
                var pts = pth.Points.Select(p => WorldToMap(p.X, p.Z)).ToArray();
                if (pts.Length >= 2) g.DrawLines(pp, pts);
                foreach (var pt in pts) g.FillEllipse(Brushes.Orange, pt.X - 3, pt.Y - 3, 6, 6);
            }

        // spawn marker on room 0
        var c0 = Center(rooms[0]);
        g.FillEllipse(Brushes.DeepSkyBlue, c0.X - 7, c0.Y - 7, 14, 14);
        g.DrawString("S", small, Brushes.Black, c0.X - 4, c0.Y - 6);

        // legend
        int ly = h - 96;
        DrawLegend(g, small, Pad, ref ly);

        bmp.Save(path, ImageFormat.Png);
    }

    // ── Isometric overview of the dungeon geometry + actors ───────────────
    private static void RenderIso(Room[] rooms, string path)
    {
        const float S = 0.16f;
        float IX(float x, float z) => (x - z) * 0.866f * S;
        float IY(float x, float y, float z) => (x + z) * 0.5f * S - y * S;

        // canvas bounds from every room footprint corner (floor + ceiling)
        float minX = 1e9f, minY = 1e9f, maxX = -1e9f, maxY = -1e9f;
        foreach (var r in rooms)
            foreach (var (cx, cz) in new[] { (r.Cx - Half, r.Cz - Half), (r.Cx + Half, r.Cz - Half), (r.Cx - Half, r.Cz + Half), (r.Cx + Half, r.Cz + Half) })
                foreach (float yy in new[] { 0f, (float)Height })
                {
                    float ix = IX(cx, cz), iy = IY(cx, yy, cz);
                    minX = Math.Min(minX, ix); maxX = Math.Max(maxX, ix);
                    minY = Math.Min(minY, iy); maxY = Math.Max(maxY, iy);
                }
        int pad = 60, w = (int)(maxX - minX) + pad * 2, h = (int)(maxY - minY) + pad * 2 + 50;
        float ox = pad - minX, oy = pad - minY + 40;
        PointF P(float x, float y, float z) => new(IX(x, z) + ox, IY(x, y, z) + oy);

        using var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(16, 20, 18));
        using var title = new Font("Segoe UI", 15, FontStyle.Bold);
        g.DrawString("TEST TEMPLE — isometric overview", title, Brushes.Gainsboro, pad, 12);

        // back-to-front: smaller (gx+gz) is farther
        foreach (var r in rooms.OrderBy(r => r.Gx + r.Gz))
        {
            float x0 = r.Cx - Half, x1 = r.Cx + Half, z0 = r.Cz - Half, z1 = r.Cz + Half;
            // floor
            var floor = new[] { P(x0, 0, z0), P(x1, 0, z0), P(x1, 0, z1), P(x0, 0, z1) };
            g.FillPolygon(new SolidBrush(Color.FromArgb(52, 64, 56)), floor);
            g.DrawPolygon(new Pen(Color.FromArgb(110, 125, 112)), floor);
            // back walls (north z0, west x0) as translucent quads
            var north = new[] { P(x0, 0, z0), P(x1, 0, z0), P(x1, Height, z0), P(x0, Height, z0) };
            var west = new[] { P(x0, 0, z0), P(x0, 0, z1), P(x0, Height, z1), P(x0, Height, z0) };
            g.FillPolygon(new SolidBrush(Color.FromArgb(120, 40, 50, 44)), north);
            g.FillPolygon(new SolidBrush(Color.FromArgb(150, 34, 44, 38)), west);
            g.DrawPolygon(new Pen(Color.FromArgb(90, 105, 95)), north);
            g.DrawPolygon(new Pen(Color.FromArgb(90, 105, 95)), west);
            g.DrawString(r.Name, new Font("Segoe UI", 7.5f, FontStyle.Bold), Brushes.Gainsboro, P(x0 + 60, Height, z0 + 60));
            // actors
            foreach (var a in r.Z.Actors)
            {
                var (col, _) = MarkerFor(a);
                if (col == Color.Empty) continue;
                var pt = P(a.XPos, a.YPos + 40, a.ZPos);
                g.FillEllipse(new SolidBrush(col), pt.X - 4, pt.Y - 4, 8, 8);
                g.DrawEllipse(Pens.Black, pt.X - 4, pt.Y - 4, 8, 8);
            }
        }
        var sp = P(rooms[0].Cx, 60, rooms[0].Cz);
        g.FillEllipse(Brushes.DeepSkyBlue, sp.X - 6, sp.Y - 6, 12, 12);
        bmp.Save(path, ImageFormat.Png);
    }

    private static (Color, string) MarkerFor(ZActor a) => a.Number switch
    {
        A_BOX => (Color.Gold, "chest"),
        A_POT => (Color.SandyBrown, "pot"),
        A_ITEM00 => (Color.LimeGreen, "rupee"),
        A_SWITCH => (Color.Cyan, "eye"),
        A_GOHMA => (Color.Magenta, "boss"),
        A_DARKLINK => (Color.MediumPurple, "miniboss"),
        A_STALFOS or A_BUBBLE or A_SKULLTULA or A_FLOORMAS or A_WOLFOS or A_POE => (Color.OrangeRed, "enemy"),
        _ when a.IsTransition => (Color.Empty, ""),
        _ => (Color.Empty, ""),
    };

    private static void DrawLegend(Graphics g, Font f, int x, ref int y)
    {
        var items = new (Color, string)[]
        {
            (Color.DeepSkyBlue, "Link spawn"), (Color.OrangeRed, "enemy"), (Color.Gold, "chest"),
            (Color.SandyBrown, "pot"), (Color.LimeGreen, "rupee"), (Color.Cyan, "eye switch"),
            (Color.MediumPurple, "Dark Link"), (Color.Magenta, "Gohma"),
        };
        int cx = x;
        foreach (var (c, t) in items)
        {
            g.FillEllipse(new SolidBrush(c), cx, y, 10, 10);
            g.DrawString(t, f, Brushes.Gainsboro, cx + 13, y - 2);
            cx += 13 + (int)g.MeasureString(t, f).Width + 16;
            if (cx > 900) { cx = x; y += 18; }
        }
    }
}
