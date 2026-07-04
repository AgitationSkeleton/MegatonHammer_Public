using System;
using System.Linq;
using MegatonHammer.Export;

namespace MegatonHammer.SelfTest;

/// <summary>Headless check of the dialogue message encoder (MegatonHammer --testmessages). Verifies the
/// friendly markup lowers to the correct OoT/MM control bytes and terminators.</summary>
public static class MessageTest
{
    public static void Run()
    {
        int fail = 0;
        void Check(string label, byte[] got, byte[] want)
        {
            bool ok = got.SequenceEqual(want);
            Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {label}: {Hex(got)}" + (ok ? "" : $"  (want {Hex(want)})"));
            if (!ok) fail++;
        }

        // OoT: "Hi" + newline + red "X" → H i 0x01 0x05 01 X 0x02
        Check("OoT plain+newline+colour",
            MessageEncoder.EncodeOoT("Hi&%rX"),
            new byte[] { (byte)'H', (byte)'i', 0x01, 0x05, 0x01, (byte)'X', 0x02 });

        // OoT box break (^) → 0x04, ends with END 0x02
        Check("OoT box break",
            MessageEncoder.EncodeOoT("A^B"),
            new byte[] { (byte)'A', 0x04, (byte)'B', 0x02 });

        // MM: newline 0x11, bare colour byte, terminator 0xBF
        Check("MM plain+newline+colour",
            MessageEncoder.EncodeMM("Hi&%gX"),
            new byte[] { (byte)'H', (byte)'i', 0x11, 0x02, (byte)'X', 0xBF });

        // MM box break (^) → 0x10
        Check("MM box break",
            MessageEncoder.EncodeMM("A^B"),
            new byte[] { (byte)'A', 0x10, (byte)'B', 0xBF });

        Console.WriteLine(fail == 0
            ? "[testmessages] PASS — encoder produces correct OoT/MM control bytes."
            : $"[testmessages] FAIL — {fail} case(s) wrong.");
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    /// <summary>Structurally verify RomInjector.AppendMessages: overwrite a real message in the debug ROM,
    /// then re-locate + re-decode it and confirm the new text is present. MegatonHammer --testmsgappend [rom]</summary>
    public static void Append(string? romPath)
    {
        romPath ??= Editor.AppPaths.Rom(@"ZELOOTD.z64");
        if (!System.IO.File.Exists(romPath)) { Console.WriteLine($"[testmsgappend] ROM not found: {romPath}"); Environment.ExitCode = 1; return; }

        var data = System.IO.File.ReadAllBytes(romPath);
        var rom = new Rom.RomImage(romPath);
        var loc = Rom.MessageTableLocator.Find(data);
        if (!loc.Found) { Console.WriteLine("[testmsgappend] FAIL — table not located."); Environment.ExitCode = 1; return; }

        // Pick a real textId whose slot is comfortably large.
        var e = loc.Entries; int target = -1; uint slotLen = 0;
        for (int i = 0; i + 1 < e.Count; i++)
        { uint len = e[i + 1].Offset - e[i].Offset; if (e[i].TextId is > 0 and < 0xFFF0 && len >= 48) { target = e[i].TextId; slotLen = len; break; } }
        if (target < 0) { Console.WriteLine("[testmsgappend] FAIL — no suitable target id."); Environment.ExitCode = 1; return; }

        var body = Export.MessageEncoder.EncodeOoT("MH test sign.&Second line here.");
        int applied = Rom.RomInjector.AppendMessages(data, rom, new[] { (target, body) }, out var log);
        Console.WriteLine($"target id 0x{target:X4} (slot {slotLen} bytes); applied={applied}; {log}");

        // Re-decode the target from the mutated bytes.
        int dataStart = -1;
        foreach (var f in rom.Files) { if (f.Exists && f.Size >= (int)e[^1].Offset && f.Size <= (int)e[^1].Offset + 0x4000) { int v=(int)f.VromStart; int o=v+(int)e[2].Offset; int pr=0; for(int b=0;b<16;b++) if(data[o+b]>=0x20&&data[o+b]<0x7F)pr++; if(pr>=6){dataStart=v;break;} } }
        uint off = 0; foreach (var en in e) if (en.TextId == target) { off = en.Offset; break; }
        var sb = new System.Text.StringBuilder();
        for (int b = 0; b < (int)slotLen; b++) { byte ch = data[dataStart + (int)off + b]; if (ch == 0x02) break; sb.Append(ch >= 0x20 && ch < 0x7F ? (char)ch : '·'); }
        string got = sb.ToString();
        bool ok = applied == 1 && got.Contains("MH test sign") && got.Contains("Second line");
        Console.WriteLine($"  re-decoded: \"{got}\"");
        Console.WriteLine(ok ? "[testmsgappend] PASS — message overwritten in place and re-decodes correctly."
                             : "[testmsgappend] FAIL — re-decode mismatch.");
        Environment.ExitCode = ok ? 0 : 1;
    }

    /// <summary>Locate OoT's message table in a ROM and report stats (MegatonHammer --testmsgtable [rom]).
    /// Default ROM: the OoT debug ROM. Proves the MessageTableLocator works on a real ROM.</summary>
    public static void Table(string? romPath)
    {
        romPath ??= Editor.AppPaths.Rom(@"ZELOOTD.z64");
        if (!System.IO.File.Exists(romPath)) { Console.WriteLine($"[testmsgtable] ROM not found: {romPath}"); Environment.ExitCode = 1; return; }

        // The debug ROM is uncompressed (z64 big-endian), so the raw file bytes == the VROM image; scan
        // them directly. For compressed ROMs we'd Decompress first.
        var raw = System.IO.File.ReadAllBytes(romPath);
        var loc = Rom.MessageTableLocator.Find(raw);
        if (!loc.Found) { Console.WriteLine("[testmsgtable] FAIL — message table not located."); Environment.ExitCode = 1; return; }

        var e = loc.Entries;
        Console.WriteLine($"message table @ ROM 0x{loc.Offset:X}, {e.Count} entries, segment bank 0x{loc.Bank:X2}");
        Console.WriteLine($"  textId range: 0x{e[0].TextId:X4} .. 0x{e[^1].TextId:X4}; offset 0x{e[0].Offset:X} .. 0x{e[^1].Offset:X}");

        // Round-trip: locate the message-data file (the dmadata file of size == data span whose bytes at
        // the table's offsets decode as messages), then decode entry 0 and print it. Proves the locator
        // + format end to end against real data.
        var rom = new Rom.RomImage(romPath);
        uint dataSpan = e[^1].Offset;   // last entry's offset ≈ data size (its own message extends past)
        int dataStart = -1;
        foreach (var f in rom.Files)
        {
            if (!f.Exists) continue;
            int sz = f.Size;
            if (sz < (int)dataSpan || sz > (int)dataSpan + 0x4000) continue;   // ~matches data span
            // entry 0 begins with a textbox header byte sequence; validate a couple of entries are ASCII-ish
            int v = (int)f.VromStart;
            if (v + (int)e[2].Offset + 8 > raw.Length) continue;
            bool plausible = true;
            for (int k = 1; k <= 3 && plausible; k++)
            {
                int o = v + (int)e[k].Offset;
                int printable = 0; for (int b = 0; b < 16 && o + b < raw.Length; b++) if (raw[o + b] >= 0x20 && raw[o + b] < 0x7F) printable++;
                if (printable < 6) plausible = false;   // messages are mostly ASCII text
            }
            if (plausible) { dataStart = v; break; }
        }

        bool ok = e.Count > 1000 && e[0].Offset == 0;
        if (dataStart >= 0)
        {
            int o = dataStart + (int)e[0].Offset;
            var sb = new System.Text.StringBuilder();
            for (int b = 0; b < (int)e[1].Offset && o + b < raw.Length; b++)
            { byte ch = raw[o + b]; sb.Append(ch >= 0x20 && ch < 0x7F ? (char)ch : '·'); }
            Console.WriteLine($"  message-data file @ ROM 0x{dataStart:X}; entry[0] (id 0x{e[0].TextId:X4}) text: \"{sb}\"");
        }
        else { Console.WriteLine("  (message-data file not auto-located; table still valid)"); }

        Console.WriteLine(ok
            ? $"[testmsgtable] PASS — located OoT message table ({e.Count} entries, bank 0x{loc.Bank:X2}, offset 0)."
            : "[testmsgtable] FAIL — table stats unexpected.");
        Environment.ExitCode = ok ? 0 : 1;
    }
}
