namespace SoulRipper.Output;

// Flags sounds that are probably not real entries of the game's sound list.
internal static class Suspects
{
    // Request IDs index tables: valid IDs form a mostly contiguous run from the
    // lowest one, and values past a long silent gap read garbage pointers.
    public const int Gap = 8;

    // Values past a long run of values that produced nothing new on the same
    // queue (garbage table reads), and multi-bit values on bit-flag queues
    // (two effects requested at once). When a community rip lists the values
    // it requests on a queue, the other values of that queue are suspect.
    public static HashSet<Sound> Find(IEnumerable<Sound> sounds, IReadOnlyDictionary<ushort, HashSet<byte>>? known = null)
    {
        var suspects = new HashSet<Sound>();
        foreach (var group in sounds.GroupBy(s => s.Address))
        {
            if (known?.GetValueOrDefault(group.Key) is { } listed)
            {
                suspects.UnionWith(group.Where(s => !s.Aliases.Select(ValueOf).Prepend(s.Value).Any(listed.Contains)));
                continue;
            }
            var aliases = group.SelectMany(s => s.Aliases).Select(ValueOf).ToHashSet();
            if (IsBitFlagQueue(group.ToList()))
            {
                suspects.UnionWith(group.Where(s => !IsPowerOfTwo(s.Value)));
                continue;
            }
            var responding = group.Select(s => s.Value).Concat(aliases).Distinct().Order().ToList();
            int? cutoff = responding.Zip(responding.Skip(1))
                .Where(p => p.Second - p.First > Gap).Select(p => (int?)p.Second).FirstOrDefault();
            if (cutoff is { } c)
                suspects.UnionWith(group.Where(s => s.Value >= c));
        }
        return suspects;
    }

    private static byte ValueOf(string id) => Convert.ToByte(id.Split('_')[1], 16);

    private static bool IsPowerOfTwo(byte v) => v > 0 && (v & (v - 1)) == 0;

    // A queue where each bit requests one effect (SMB3 style): the single bits
    // all play distinct sounds and most multi-bit values repeat the sound of
    // one of their own bits.
    private static bool IsBitFlagQueue(List<Sound> group)
    {
        var singles = group.Count(s => IsPowerOfTwo(s.Value));
        var multi = group.SelectMany(s => s.Aliases.Select(a => (Owner: s, Value: ValueOf(a))))
            .Where(a => !IsPowerOfTwo(a.Value)).ToList();
        var ownBit = multi.Count(a => IsPowerOfTwo(a.Owner.Value) && (a.Value & a.Owner.Value) != 0);
        var others = multi.Count - ownBit + group.Count(s => !IsPowerOfTwo(s.Value));
        return singles >= 4 && ownBit > others;
    }
}
