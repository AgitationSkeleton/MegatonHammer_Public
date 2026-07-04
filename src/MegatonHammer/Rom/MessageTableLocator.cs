using System.Collections.Generic;

namespace MegatonHammer.Rom;

/// <summary>
/// Locates OoT's <c>sNesMessageEntryTable</c> in a flat ROM, version-independently. Each entry is 8
/// bytes — <c>[ id(u16) | opts(u8) | 0x00 | bank(u8) | offset(u24) ]</c> — where the message text lives
/// at <c>(message-data file ROM start) + offset</c>; <c>opts = (boxType&lt;&lt;4) | yPos</c>. The table is
/// sorted by ascending offset and terminated by an <c>id == 0xFFFF</c> entry. The <b>bank</b> is the
/// segment number the build assigns the message-data file and varies by build (PAL/debug = 0x07, others
/// 0x08), so it is auto-detected rather than assumed. Anchored on the terminator + back-walked so a table
/// that doesn't form a clean forward run (decoys, special trailing entries) is still found. See
/// docs/dialogue-authoring-plan.md.
/// </summary>
public static class MessageTableLocator
{
    public const int EntrySize = 8;

    public readonly record struct Entry(ushort TextId, byte Opts, uint Offset);
    public readonly record struct Result(int Offset, byte Bank, IReadOnlyList<Entry> Entries)
    {
        public bool Found => Offset >= 0;
    }

    private const int MinRun = 200;   // the real table has ~2000+ entries; rules out short decoys

    public static Result Find(byte[] d)
    {
        int bestStart = -1, bestN = 0; byte bestBank = 0;
        // Anchor on every terminator candidate (id == 0xFFFF) and back-walk a constant-bank, pad-0,
        // descending-offset run. The longest such run reaching offset 0 is the message table.
        for (int q = 4; q + EntrySize <= d.Length; q += 4)
        {
            if (d[q] != 0xFF || d[q + 1] != 0xFF) continue;
            int e = q - EntrySize;
            byte bank = d[e + 4];
            if (bank == 0 || bank > 0x0F || d[e + 3] != 0) continue;

            long prevOff = -1; int p = e, n = 0; uint firstOff = 0;
            while (p >= 0)
            {
                if (d[p + 3] != 0 || d[p + 4] != bank) break;
                if (d[p] == 0xFF && d[p + 1] == 0xFF) break;   // hit an earlier terminator
                uint off = (uint)((d[p + 5] << 16) | (d[p + 6] << 8) | d[p + 7]);
                if (prevOff >= 0 && off > prevOff) break;       // offsets must descend going back
                prevOff = off; firstOff = off; n++; p -= EntrySize;
            }
            if (n > bestN && firstOff == 0) { bestN = n; bestStart = q - EntrySize * n; bestBank = bank; }
        }

        if (bestStart < 0 || bestN < MinRun) return new Result(-1, 0, []);

        var entries = new List<Entry>(bestN);
        for (int i = 0; i < bestN; i++)
        {
            int p = bestStart + i * EntrySize;
            entries.Add(new Entry((ushort)((d[p] << 8) | d[p + 1]), d[p + 2],
                                  (uint)((d[p + 5] << 16) | (d[p + 6] << 8) | d[p + 7])));
        }
        return new Result(bestStart, bestBank, entries);
    }
}
