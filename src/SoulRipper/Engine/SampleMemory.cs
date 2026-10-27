namespace SoulRipper;

// DMC samples play from cartridge memory, which a VGM player lacks: a VGM
// loads their bytes into the player's APU RAM with data blocks, and that RAM
// keeps them for later sample starts.
internal static class SampleMemory
{
    // The memory a sample plays; past $FFFF the DMC wraps to $8000.
    public static IEnumerable<(ushort Address, int Length)> Ranges(ushort start, int length)
    {
        var first = Math.Min(length, 0x10000 - start);
        yield return (start, first);
        if (first < length)
            yield return (0x8000, length - first);
    }

    // The blocks a VGM needs so that every sample start finds its bytes
    // loaded: a sample is loaded again only when other bytes took its memory
    // (games bank-switch samples into the same addresses). The loop loads its
    // samples from scratch, because on later passes the RAM holds whatever
    // the end of the loop left, not what was loaded before it.
    public static List<(int Frame, ushort Address, byte[] Data)> Blocks(
        IEnumerable<(int Frame, ushort Address, byte[] Data)> played, int frames, int? loopStart)
    {
        var blocks = new List<(int, ushort, byte[])>();
        var ram = new short[0x10000];
        Array.Fill(ram, (short)-1);
        var looping = false;
        foreach (var (frame, address, data) in played.Where(p => p.Frame < frames).OrderBy(p => p.Frame))
        {
            if (!looping && frame >= loopStart)
            {
                Array.Fill(ram, (short)-1);
                looping = true;
            }
            if (data.Select((b, i) => ram[(ushort)(address + i)] == b).All(loaded => loaded))
                continue;
            for (var i = 0; i < data.Length; i++)
                ram[(ushort)(address + i)] = data[i];
            blocks.Add((frame, address, data));
        }
        return blocks;
    }
}
