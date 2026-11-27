using System.Text.Json;
using SoulRipper.Hints;

namespace SoulRipper;

// A curated description of a game: how to reach a playable state and what
// its sound requests are called. Every field is optional. A profile that
// lists the CRC32s of its ROMs ("crc") ships inside the tool and is used for
// them without --profile.
internal sealed record Profile(string? Name, string Author, string Date, Reach? Reach,
    IReadOnlyList<(ushort Queue, byte Value, string Name, bool IsRoutine)> Requests, IReadOnlyList<string> Crcs)
{
    public static readonly Profile Empty = new(null, "", "", null, Array.Empty<(ushort, byte, string, bool)>(), Array.Empty<string>());

    public static Profile Load(string path) => Parse(File.ReadAllText(path));

    // The built-in profile for a ROM, by the CRC32s No-Intro lists for it.
    public static Profile? ForRom(byte[] rom) => ForCrcs(GameHints.RomCrcs(rom));

    public static Profile? ForCrcs(IReadOnlySet<string> crcs) => BuiltIn().FirstOrDefault(p => p.Crcs.Any(crcs.Contains));

    public static IEnumerable<Profile> BuiltIn()
    {
        var assembly = typeof(Profile).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("profiles/", StringComparison.Ordinal)).Order())
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            yield return Parse(reader.ReadToEnd());
        }
    }

    public static Profile Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Text(string key) => root.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";

        Reach? reach = root.TryGetProperty("reach", out var r) ? Reach.FromJson(r)
            : root.TryGetProperty("boot_frames", out var b) ? Reach.Default with { Boot = b.GetInt32() }
            : null;

        var requests = new List<(ushort, byte, string, bool)>();
        foreach (var list in new[] { "music", "sfx" })
        {
            if (!root.TryGetProperty(list, out var entries)) continue;
            foreach (var e in entries.EnumerateArray())
            {
                if (!e.TryGetProperty("value", out var v)) continue;
                var isRoutine = !e.TryGetProperty("queue", out var q);
                if (isRoutine && !e.TryGetProperty("routine", out q)) continue;
                var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                requests.Add(((ushort)Number(q), (byte)Number(v), name, isRoutine));
            }
        }
        var crcs = root.TryGetProperty("crc", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(e => e.GetString()!.ToUpperInvariant()).ToList()
            : new List<string>();
        return new Profile(Text("name") is { Length: > 0 } game ? game : null, Text("author"), Text("date"), reach, requests, crcs);
    }

    // Only RAM queues go to --queues / extra; routine entries are named without
    // poking ROM addresses.
    public IEnumerable<ushort> Queues => Requests.Where(r => !r.IsRoutine).Select(r => r.Queue).Distinct();

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
