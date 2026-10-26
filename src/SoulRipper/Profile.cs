using System.Text.Json;

namespace SoulRipper;

// A curated description of a game: how to reach a playable state and what
// its sound requests are called. Every field is optional.
internal sealed record Profile(string? Name, string Author, string Date, Reach? Reach,
    IReadOnlyList<(ushort Queue, byte Value, string Name)> Requests)
{
    public static readonly Profile Empty = new(null, "", "", null, Array.Empty<(ushort, byte, string)>());

    public static Profile Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        string Text(string key) => root.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";

        Reach? reach = root.TryGetProperty("reach", out var r) ? Reach.FromJson(r)
            : root.TryGetProperty("boot_frames", out var b) ? Reach.Default with { Boot = b.GetInt32() }
            : null;

        var requests = new List<(ushort, byte, string)>();
        foreach (var list in new[] { "music", "sfx" })
        {
            if (!root.TryGetProperty(list, out var entries)) continue;
            foreach (var e in entries.EnumerateArray())
            {
                if (!e.TryGetProperty("queue", out var q) || !e.TryGetProperty("value", out var v)) continue;
                var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                requests.Add(((ushort)Number(q), (byte)Number(v), name));
            }
        }
        return new Profile(Text("name") is { Length: > 0 } game ? game : null, Text("author"), Text("date"), reach, requests);
    }

    public IEnumerable<ushort> Queues => Requests.Select(r => r.Queue).Distinct();

    // Sound id -> curated name.
    public Dictionary<string, string> Names() => Requests
        .Where(r => r.Name.Length > 0)
        .GroupBy(r => $"{r.Queue:X4}_{r.Value:X2}")
        .Where(g => g.First().Name != g.Key)
        .ToDictionary(g => g.Key, g => g.First().Name);

    // 1265, "0x04F1" or "1265".
    private static int Number(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? e.GetInt32() : Options.ParseNumber(e.GetString()!);
}
