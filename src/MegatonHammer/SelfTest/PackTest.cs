using System.IO.Compression;
using MegatonHammer.Editor;
using MegatonHammer.Export;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>
/// Verifies the editor-side packaging gaps: multi-scene O2R packing (N custom scenes, each at its own
/// reserved append slot, listed in mh/info) and custom-music sequence injection (a sequence resource
/// added + recorded in mh/info). Engine-side registration is verified separately in-game.
/// Run: MegatonHammer --testpack
/// </summary>
public static class PackTest
{
    public static void Run()
    {
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; Console.WriteLine($"  PASS {what}"); } else { fail++; Console.WriteLine($"  FAIL {what}"); } }

        string o2r = Path.Combine(Path.GetTempPath(), "mh_packtest_" + Guid.NewGuid().ToString("N")[..8] + ".o2r");
        try
        {
            // Two minimal scenes, each with a brush box so the OTR builder emits geometry.
            ZScene MakeScene(string name)
            {
                var s = new ZScene(name);
                if (s.Rooms.Count == 0) s.AddRoom();
                s.Rooms[0].Geometry.Add(Solid.CreateBox(new Vector3(-100, 0, -100), new Vector3(100, 50, 100)));
                return s;
            }
            var scenes = new[] { MakeScene("Castle"), MakeScene("Dungeon") };
            var cfg = new PlaytestConfig { Append = true };

            O2RPacker.PackOtrMulti(scenes, o2r, cfg, mm: false, texResolver: null);

            using (var zip = ZipFile.OpenRead(o2r))
            {
                bool s0 = zip.Entries.Any(e => e.FullName.Contains("mh_append_0"));
                bool s1 = zip.Entries.Any(e => e.FullName.Contains("mh_append_1"));
                Check(s0 && s1, "both scenes packed to mh_append_0 and mh_append_1");
                var info = zip.GetEntry(O2RPacker.InfoEntry);
                using var r = new StreamReader(info!.Open());
                string json = r.ReadToEnd();
                Check(json.Contains("\"multi\":true") && json.Contains("\"appendCount\":2"), "mh/info marks multi + appendCount=2");
                Check(json.Contains("\"name\":\"Castle\"") && json.Contains("\"name\":\"Dungeon\""), "mh/info scenes manifest lists both names");
            }

            // Inject a custom sequence and verify it lands as an OSEQ resource + is recorded.
            byte[] seq = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            int host = SequenceInjector.HostSeqId(mm: false);
            SequenceInjector.PackInto(o2r, host, seq, fontId: 3);
            using (var zip = ZipFile.OpenRead(o2r))
            {
                var seqEntry = zip.GetEntry(SequenceInjector.ResourcePath);
                // OSEQ = 64-byte header + u32 size + 64 seqData + seqNumber/medium/cachePolicy + u32 numFonts + 1 font
                Check(seqEntry != null && seqEntry.Length == 64 + 4 + 64 + 3 + 4 + 1, "OSEQ sequence resource packed");
                using var r = new StreamReader(zip.GetEntry(O2RPacker.InfoEntry)!.Open());
                string json = r.ReadToEnd();
                Check(json.Contains("\"music\"") && json.Contains($"\"seqId\":{host}") && json.Contains("\"fontId\":3"), "mh/info records the injected sequence");
            }

            Console.WriteLine($"\n==== {(fail == 0 ? "ALL PASS" : $"{fail} FAILED")} ({pass} passed) ====");
        }
        finally { try { File.Delete(o2r); File.Delete(o2r + ".mhbak"); } catch { } }
    }
}
