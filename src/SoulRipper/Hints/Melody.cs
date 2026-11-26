namespace SoulRipper.Hints;

// The pitches each channel plays, in order, without timing or repeats. Two
// logs of the same tune from different players (another emulator, a logger
// that drops redundant writes, the driver rewriting registers every frame)
// differ in their raw writes but keep this: the Zelda pack on vgmrips scores
// 0.15 against our Overworld capture on raw writes and 0.99 on pitches.
internal sealed class Melody
{
    // Pitch registers: (channel, low byte?) for the NES APU and the Game Boy DMG.
    private static readonly Dictionary<ushort, (int Channel, bool Low)> Pitch = new()
    {
        [0x4002] = (0, true), [0x4003] = (0, false),
        [0x4006] = (1, true), [0x4007] = (1, false),
        [0x400A] = (2, true), [0x400B] = (2, false),
        [0x400E] = (3, true),
        [0xFF13] = (0, true), [0xFF14] = (0, false),
        [0xFF18] = (1, true), [0xFF19] = (1, false),
        [0xFF1D] = (2, true), [0xFF1E] = (2, false),
        [0xFF22] = (3, true),
    };

    // Enough of a tune to tell it apart while keeping comparisons cheap.
    public const int MaxNotes = 120;

    private readonly List<int>[] channels;

    private Melody(List<int>[] channels) => this.channels = channels;

    public int Count => channels.Sum(c => c.Count);

    public static Melody Of(IEnumerable<(ushort Address, byte Value)> writes)
    {
        var channels = Enumerable.Range(0, 4).Select(_ => new List<int>()).ToArray();
        var low = new int[4];
        var high = new int[4];
        foreach (var (address, value) in writes)
        {
            if (!Pitch.TryGetValue(address, out var p))
                continue;
            if (p.Low) low[p.Channel] = value;
            else high[p.Channel] = value & 7;
            var notes = channels[p.Channel];
            var pitch = high[p.Channel] << 8 | low[p.Channel];
            if (notes.Count < MaxNotes && (notes.Count == 0 || notes[^1] != pitch))
                notes.Add(pitch);
        }
        return new Melody(channels);
    }

    // Matched pitches over all pitches, channel by channel (0..1).
    public double Similarity(Melody other)
    {
        var total = Count + other.Count;
        if (total == 0)
            return 0;
        var matched = 0.0;
        for (var c = 0; c < channels.Length; c++)
        {
            var (a, b) = (channels[c], other.channels[c]);
            if (a.Count > 0 && b.Count > 0)
                matched += SequenceMatcher.Ratio(a, b) * (a.Count + b.Count);
        }
        return matched / total;
    }

    // Upper bound of Similarity, to skip pairs that cannot reach a threshold.
    public double Bound(Melody other)
    {
        var total = Count + other.Count;
        return total == 0 ? 0 : 2.0 * channels.Zip(other.channels, (a, b) => Math.Min(a.Count, b.Count)).Sum() / total;
    }
}
