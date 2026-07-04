namespace MegatonHammer.Editor;

/// <summary>
/// The Zelda 64 analogue of Valve Hammer's entity Input/Output graph. Source entities wire up with
/// named outputs→inputs; OoT/MM "wire up" implicitly through shared switch / chest / collectible
/// flags (one actor SETS a flag, another READS it) and through the scene's transition-actor / exit
/// tables. This scans every placed actor, decodes its flag fields via <see cref="ActorParamSchema"/>,
/// and groups actors by the flag they touch — so the editor can show "who sets/reads switch flag 12".
/// </summary>
public static class FlagConnectionAnalyzer
{
    public sealed record Usage(ZActor Actor, ActorParamSchema.FlagRole Role, string FieldName);

    public sealed record FlagGroup(ActorParamSchema.FlagKind Kind, int Index)
    {
        public List<Usage> Users { get; } = [];
        public bool HasSetter => Users.Any(u => u.Role is ActorParamSchema.FlagRole.Setter or ActorParamSchema.FlagRole.Both);
        public bool HasReader => Users.Any(u => u.Role is ActorParamSchema.FlagRole.Reader or ActorParamSchema.FlagRole.Both);
    }

    public sealed record Exit(string Description, ZActor? Actor, Solid? Trigger);

    /// <summary>One drawable connection: a setter actor → a reader actor over a shared flag (the OoT/MM
    /// analogue of a Hammer output→input wire).</summary>
    public sealed record Link(ZActor From, ZActor To, ActorParamSchema.FlagKind Kind, int Index);

    /// <summary>Setter→reader links across every shared flag, for the viewport connection-line overlay.</summary>
    public static List<Link> Links(IEnumerable<ZActor> actors, bool isOoT)
    {
        var links = new List<Link>();
        foreach (var g in Analyze(actors, isOoT))
        {
            // A CHEST/treasure flag is a chest's own opened-state (it sets AND reads its OWN flag), never a
            // signal TO another actor — so two chests sharing one is a flag COLLISION, not a logical wire.
            // Don't draw connection lines for it (that made rows of default-flag chests appear "linked"); the
            // real collision is surfaced as a SceneValidator warning instead.
            if (g.Kind == ActorParamSchema.FlagKind.Chest) continue;
            var setters = g.Users.Where(u => u.Role is ActorParamSchema.FlagRole.Setter or ActorParamSchema.FlagRole.Both)
                                 .Select(u => u.Actor).Distinct().ToList();
            var readers = g.Users.Where(u => u.Role is ActorParamSchema.FlagRole.Reader or ActorParamSchema.FlagRole.Both)
                                 .Select(u => u.Actor).Distinct().ToList();
            foreach (var s in setters)
                foreach (var r in readers)
                    if (!ReferenceEquals(s, r)) links.Add(new Link(s, r, g.Kind, g.Index));
        }
        return links;
    }

    /// <summary>One end of a per-actor connection: the OTHER actor wired to the selected one over a shared
    /// flag, plus which flag (the Hammer "My Output -> Target -> Their Input" tuple, flag-based).</summary>
    public sealed record IoEndpoint(ZActor Other, ActorParamSchema.FlagKind Kind, int Index, string FieldName, string OtherFieldName);

    /// <summary>Per-actor I/O for the Hammer-style Outputs/Inputs view: every flag this actor SETS paired
    /// with the actors that READ it (Outputs), and every flag it READS paired with the actors that SET it
    /// (Inputs). Empty lists when the actor isn't actually wired to anything — so the dialog shows "(none)"
    /// rather than implying a connection that the viewport wouldn't draw.</summary>
    public static (List<IoEndpoint> Outputs, List<IoEndpoint> Inputs) ConnectionsFor(
        ZActor actor, IEnumerable<ZActor> actors, bool isOoT)
    {
        var outs = new List<IoEndpoint>();
        var ins = new List<IoEndpoint>();
        var actorList = actors as IReadOnlyCollection<ZActor> ?? actors.ToList();
        foreach (var g in Analyze(actorList, isOoT))
        {
            var mine = g.Users.Where(u => ReferenceEquals(u.Actor, actor)).ToList();
            if (mine.Count == 0) continue;
            bool iSet  = mine.Any(u => u.Role is ActorParamSchema.FlagRole.Setter or ActorParamSchema.FlagRole.Both);
            bool iRead = mine.Any(u => u.Role is ActorParamSchema.FlagRole.Reader or ActorParamSchema.FlagRole.Both);
            string myField = mine[0].FieldName;
            foreach (var u in g.Users)
            {
                if (ReferenceEquals(u.Actor, actor)) continue;
                bool oSet  = u.Role is ActorParamSchema.FlagRole.Setter or ActorParamSchema.FlagRole.Both;
                bool oRead = u.Role is ActorParamSchema.FlagRole.Reader or ActorParamSchema.FlagRole.Both;
                if (iSet && oRead) outs.Add(new IoEndpoint(u.Actor, g.Kind, g.Index, myField, u.FieldName));
                if (iRead && oSet) ins.Add(new IoEndpoint(u.Actor, g.Kind, g.Index, myField, u.FieldName));
            }
        }
        return (outs, ins);
    }

    /// <summary>Groups all flag usages found across the actors. A 6-bit switch field set to 0x3F is
    /// the game's "no flag" sentinel and is skipped.</summary>
    public static List<FlagGroup> Analyze(IEnumerable<ZActor> actors, bool isOoT)
    {
        var map = new Dictionary<(ActorParamSchema.FlagKind, int), FlagGroup>();
        foreach (var a in actors)
        {
            var def = ActorParamSchema.For(isOoT, a.Number);
            if (def == null) continue;
            foreach (var f in def.Fields)
            {
                if (f.Flag == ActorParamSchema.FlagKind.None) continue;
                // Most flags live in the params (a.Variable); a few (En_Box falling chests, Obj_Kibako2)
                // keep the flag in the transform's Rot Z instead — read from there when FromRotZ is set.
                int idx = f.Get(f.FromRotZ ? (ushort)a.ZRot : a.Variable);
                // All-ones in a switch/collectible field is the game's "no flag" sentinel (0x3F for a
                // 6-bit field, 0x7F for 7-bit MM, 0xFF for 8-bit). Length-aware so 8-bit fields work too.
                if (f.Flag is ActorParamSchema.FlagKind.Switch or ActorParamSchema.FlagKind.Collectible && idx == f.Mask) continue;
                // Collectible flag 0 is ALSO "no flag": Flags_SetCollectible is a no-op for flag 0 and
                // Flags_GetCollectible(0) is meaningless, so pots / crates / En_Item00 that leave the field
                // at 0 don't track collection — they must NOT be wired (otherwise every rupee/heart pot
                // cross-links to every other as a phantom collectible-0 setter+reader → wires everywhere).
                if (f.Flag is ActorParamSchema.FlagKind.Collectible && idx == 0) continue;
                // Some flag fields are only a flag under a sibling-field condition (mainly doors, whose
                // low bits double as a text/message id). Skip when the condition isn't met → no phantom wire.
                if (!FlagFieldActive(a, def, f)) continue;
                var key = (f.Flag, idx);
                if (!map.TryGetValue(key, out var g)) { g = new FlagGroup(f.Flag, idx); map[key] = g; }
                g.Users.Add(new Usage(a, f.Role, f.Name));
            }
        }
        return map.Values.OrderBy(g => g.Kind).ThenBy(g => g.Index).ToList();
    }

    /// <summary>Some "flag" fields are only a flag under a sibling-field condition; otherwise the bits mean
    /// something else (a text id, a key) and must NOT generate wires. Mainly doors, whose low 6 bits are a
    /// switch flag only for the locked/switch door types and a message/text id otherwise (z_en_door.c /
    /// z_door_shutter.c). Returns true when the field genuinely acts as its flag for this actor.</summary>
    private static bool FlagFieldActive(ZActor a, ActorParamSchema.Def def, ActorParamSchema.Field f)
    {
        int DoorTypeOf(int shift, int len) =>
            def.Fields.FirstOrDefault(x => x.Name == "Door type") is { } dt ? dt.Get(a.Variable) : -1;

        // En_Door (OoT 0x0009 / MM 0x0005): low-6-bits is a SWITCH flag only for DOOR_LOCKED (type 1);
        // every other type reads it as actor.textId (a message id) — z_en_door.c:167/177. Don't wire those.
        if (a.Number is 0x0009 or 0x0005 && f.Name.StartsWith("Switch flag"))
            return DoorTypeOf(7, 3) == 1;

        // Door_Shutter (OoT 0x002E / MM 0x001E): switchFlag is used only by FRONT_SWITCH(2) /
        // FRONT_SWITCH_BACK_CLEAR(7) (readers) and BOSS(5) / KEY_LOCKED(0xB) (setters on open). FRONT_CLEAR(1)
        // uses the ROOM-clear flag and BACK_LOCKED(3) a small key — neither is a switch flag.
        if (a.Number is 0x002E or 0x001E && f.Name == "Switch flag")
            return DoorTypeOf(6, 4) is 2 or 5 or 7 or 0xB;

        // En_Box: the Rot-Z switch flag is read only by the switch-flag chest types (3 FALL_BIG, 8 FALL_SMALL,
        // 11 SWITCH_FLAG_BIG). Other chest types leave Rot Z 0/unused — don't wire them.
        if (a.Number is 0x000A or 0x0006 && f.Name.StartsWith("Switch flag (Rot Z)"))
            return (def.Fields.FirstOrDefault(x => x.Name == "Chest type") is { } ct ? ct.Get(a.Variable) : -1) is 3 or 8 or 11;

        // Obj_Hsblock (0x012D): the switch flag is read ONLY by the sinking-post type (params & 3 == 1); the
        // plain post (0) and hookshot target (2) ignore it (z_obj_hsblock.c:85-96 — only case 1 reads
        // Flags_GetSwitch). So a row of plain hookshot posts must NOT wire to each other (or to anything) via a
        // shared default switch flag 0 — the reported "same-type actors auto-link" bug.
        if (a.Number == 0x012D && f.Name == "Switch flag")
            return (a.Variable & 3) == 1;

        return true;
    }

    /// <summary>Full analysis including the room-clear ("kill all enemies in this room") channel, which
    /// is keyed by ROOM NUMBER rather than a params field — so it needs room context. Door_Shutter
    /// FRONT_CLEAR doors and room-clear chests are surfaced as readers of their room's clear flag (the
    /// setter is the engine: the last enemy in the room dying).</summary>
    public static List<FlagGroup> Analyze(MapDocument doc, bool isOoT)
    {
        var groups = Analyze(doc.AllActors, isOoT);
        var byKey = groups.ToDictionary(g => (g.Kind, g.Index));
        for (int room = 0; room < doc.Scene.Rooms.Count; room++)
            foreach (var a in doc.Scene.Rooms[room].Actors)
            {
                if (!IsRoomClearReader(isOoT, a)) continue;
                var key = (ActorParamSchema.FlagKind.Clear, room);
                if (!byKey.TryGetValue(key, out var g))
                { g = new FlagGroup(ActorParamSchema.FlagKind.Clear, room); byKey[key] = g; groups.Add(g); }
                g.Users.Add(new Usage(a, ActorParamSchema.FlagRole.Reader, "room clear"));
            }
        return groups.OrderBy(g => g.Kind).ThenBy(g => g.Index).ToList();
    }

    /// <summary>True if the actor gates on its room's clear flag: Door_Shutter type 1 (FRONT_CLEAR) or
    /// En_Box chest types 1/7 (appear on room clear).</summary>
    private static bool IsRoomClearReader(bool isOoT, ZActor a)
    {
        var def = ActorParamSchema.For(isOoT, a.Number);
        if (def == null) return false;
        if (a.Number is 0x002E)   // Door_Shutter (OoT)
        {
            var t = def.Fields.FirstOrDefault(f => f.Name == "Door type");
            return t != null && t.Get(a.Variable) == 1;
        }
        if (a.Number is 0x000A or 0x0006)   // En_Box (OoT / MM)
        {
            var t = def.Fields.FirstOrDefault(f => f.Name == "Chest type");
            return t != null && t.Get(a.Variable) is 1 or 7;
        }
        return false;
    }

    /// <summary>Exits/warps: trigger brushes carrying a destination entrance, plus transition
    /// actors (doors / room-load planes) that link rooms via the scene's transition-actor table.</summary>
    public static List<Exit> Exits(IEnumerable<Solid> solids, IEnumerable<ZActor> actors, bool isOoT)
    {
        var list = new List<Exit>();
        foreach (var s in solids)
            if (s.IsTrigger)
                list.Add(new Exit(
                    s.ExitEntrance >= 0 ? $"Trigger volume → entrance 0x{s.ExitEntrance:X4}" : "Trigger volume → void out",
                    null, s));
        foreach (var a in actors)
        {
            var def = ActorParamSchema.For(isOoT, a.Number);
            var tf = def?.Fields.FirstOrDefault(f => f.Name == "Transition index");
            if (tf != null)
                list.Add(new Exit($"{def!.Title} — transition #{tf.Get(a.Variable)}", a, null));
        }
        return list;
    }
}
