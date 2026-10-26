namespace SoulRipper.Hints;

// Matches the tracks of a community rip with the sounds discovery captured.
internal static class TrackPairing
{
    // Minimum similarity of register writes to pair a rip track with a capture.
    public const double MatchThreshold = 0.6;

    // {sound id: title}. A track is paired with the sound whose request its
    // INIT stores in RAM, or else with the most similar capture.
    public static Dictionary<string, string> Pair(IReadOnlyList<Sound> sounds, PlayedRip played, IReadOnlyList<PlaylistTrack> playlist)
    {
        var titles = playlist.ToDictionary(t => t.Track, t => t.Title);
        var byRequest = new Dictionary<(ushort, byte), string>();
        foreach (var s in sounds)
            foreach (var id in s.Aliases.Prepend(s.Id))
                byRequest[ParseId(id)] = s.Id;
        var queues = byRequest.Keys.Select(k => k.Item1).ToHashSet();
        var signatures = sounds.ToDictionary(s => s.Id, s => Signature(s.Writes));

        var names = new Dictionary<string, string>();
        var scored = new List<(double Score, string Id, string Title)>();
        foreach (var t in played.Tracks)
        {
            if (!titles.TryGetValue(t.Track, out var title) || title.Length == 0)
                continue;
            var stored = t.Ram.Where(w => queues.Contains(w.Address))
                .Select(w => byRequest.GetValueOrDefault((w.Address, w.Value))).OfType<string>().ToList();
            if (stored.Count > 0)
            {
                names.TryAdd(stored[^1], title);
                continue;
            }
            var signature = Signature(t.Writes);
            foreach (var (id, other) in signatures)
                scored.Add((Similarity(signature, other), id, title));
        }

        foreach (var (score, id, title) in scored.OrderByDescending(x => x.Score)
                     .ThenByDescending(x => x.Id, StringComparer.Ordinal).ThenByDescending(x => x.Title, StringComparer.Ordinal))
        {
            if (score < MatchThreshold)
                break;
            if (!names.ContainsKey(id) && !names.ContainsValue(title))
                names[id] = title;
        }
        return names;
    }

    // RAM bytes the rip's INIT leaves with many different values across
    // tracks: the game's request bytes (a rip often spreads music and effects
    // over several), to probe even if analysis did not spot them.
    public static List<ushort> RequestBytes(PlayedRip played, int limit = 24)
    {
        bool IsStack(ushort a) => played.System == "nes" && a is >= 0x100 and < 0x200;
        var values = new Dictionary<ushort, HashSet<byte>>();
        foreach (var t in played.Tracks)
        {
            var last = new Dictionary<ushort, byte>();
            foreach (var (a, v) in t.Ram)
                if (!IsStack(a)) last[a] = v;
            foreach (var (a, v) in last)
            {
                if (!values.TryGetValue(a, out var set)) values[a] = set = new HashSet<byte>();
                set.Add(v);
            }
        }
        return values.Where(kv => kv.Value.Count >= 3)
            .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key)
            .Take(limit).Select(kv => kv.Key).Order().ToList();
    }

    // Values the rip's INIT leaves in each of its request bytes: the requests
    // the game really makes there.
    public static Dictionary<ushort, HashSet<byte>> KnownRequests(PlayedRip played)
    {
        var queues = RequestBytes(played).ToHashSet();
        var known = new Dictionary<ushort, HashSet<byte>>();
        foreach (var t in played.Tracks)
            foreach (var (a, v) in t.Ram.Where(w => queues.Contains(w.Address)).GroupBy(w => w.Address).Select(g => g.Last()))
            {
                if (!known.TryGetValue(a, out var set)) known[a] = set = new HashSet<byte>();
                set.Add(v);
            }
        return known;
    }

    private static (ushort, byte) ParseId(string id)
    {
        var parts = id.Split('_');
        return (Convert.ToUInt16(parts[0], 16), Convert.ToByte(parts[1], 16));
    }

    // Register writes in order, without timing: what the driver played.
    private static List<(ushort, byte)> Signature(IEnumerable<(int Frame, ushort Address, byte Value)> writes) =>
        writes.Take(400).Select(w => (w.Address, w.Value)).ToList();

    private static double Similarity(List<(ushort, byte)> a, List<(ushort, byte)> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : SequenceMatcher.Ratio(a, b);
}
