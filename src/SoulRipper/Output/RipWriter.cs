using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SoulRipper.Output;

// A written (or skipped) sound, as listed in sounds.json.
internal sealed record RipEntry(Sound Sound, string? Name, string? File, bool Suspect, double Seconds, double? LoopSeconds);

// Writes a discovery result: <kind>/NN - Name.vgm for every sound,
// sounds.json and a draft profile.
internal static class RipWriter
{
    private static readonly Dictionary<string, string> KindDirs = new() { ["music"] = "music", ["jingle"] = "jingles", ["sfx"] = "sfx" };
    private static readonly Dictionary<string, string> KindLabels = new() { ["music"] = "Music", ["jingle"] = "Jingle", ["sfx"] = "SFX" };
    public static readonly Dictionary<string, string> SystemNames = new() { ["nes"] = "NES / Famicom", ["gb"] = "Game Boy" };

    // `known` lists, per request byte, the values a community rip requests.
    public static List<RipEntry> Write(RipResult rip, string outDir, IReadOnlyDictionary<string, string> names, bool keepSuspect,
        IReadOnlyDictionary<ushort, HashSet<byte>>? known = null)
    {
        var sounds = rip.Sounds.Where(s => KindDirs.ContainsKey(s.Kind)).ToList();
        var suspects = Suspects.Find(sounds, known);
        var entries = new List<RipEntry>();
        var counters = new Dictionary<string, int>();

        foreach (var s in sounds.OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(suspects.Contains)
                     .ThenBy(s => s.Address).ThenBy(s => s.Value))
        {
            var suspect = suspects.Contains(s);
            var seconds = Math.Round(s.Frames / rip.FramesPerSecond, 2);
            double? loopSeconds = s.LoopFrames is { } lf ? Math.Round(lf / rip.FramesPerSecond, 2) : null;
            if (suspect && !keepSuspect)
            {
                entries.Add(new RipEntry(s, null, null, true, seconds, loopSeconds));
                continue;
            }

            var folder = KindDirs[s.Kind] + (suspect ? "/suspect" : "");
            var index = counters[folder] = counters.GetValueOrDefault(folder) + 1;
            var name = NameOf(s, names) ?? $"{KindLabels[s.Kind]} {index:00}";
            var file = $"{folder}/{SafeFileName($"{index:00} - {name}")}.vgm";
            var gd3 = new Gd3(name, rip.Game, SystemNames[rip.System], rip.Author, rip.Date,
                $"Ripped by SoulRipper: request 0x{s.Address:X4} = 0x{s.Value:X2}");
            var path = Path.Combine(outDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, VgmWriter.Encode(rip.System, s.Init, s.Writes, s.Frames, s.LoopStart, gd3));
            entries.Add(new RipEntry(s, name, file, suspect, seconds, loopSeconds));
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outDir, "sounds.json"), new JsonObject
        {
            ["game"] = rip.Game,
            ["system"] = rip.System,
            ["sounds"] = new JsonArray(entries.Select(e => (JsonNode)Json(e)).ToArray()),
        }.ToJsonString(options));
        File.WriteAllText(Path.Combine(outDir, "profile.json"), DraftProfile(rip, entries).ToJsonString(options));
        return entries;
    }

    private static string? NameOf(Sound s, IReadOnlyDictionary<string, string> names) =>
        s.Aliases.Prepend(s.Id).Select(id => names.GetValueOrDefault(id)).FirstOrDefault(n => n is not null);

    public static string SafeFileName(string text) => Regex.Replace(text, "[\\\\/:*?\"<>|]+", "_").Trim();

    private static JsonObject Json(RipEntry e)
    {
        var s = e.Sound;
        var json = new JsonObject
        {
            ["id"] = s.Id,
            ["name"] = e.Name,
            ["kind"] = s.Kind,
            ["queue"] = $"0x{s.Address:X4}",
            ["value"] = s.Value,
            ["seconds"] = e.Seconds,
            ["end"] = s.End,
            ["channels"] = new JsonArray(s.Channels.Select(c => (JsonNode)c).ToArray()),
            ["aliases"] = new JsonArray(s.Aliases.Select(a => (JsonNode)a).ToArray()),
            ["suspect"] = e.Suspect,
            ["file"] = e.File,
        };
        if (e.LoopSeconds is { } loop) json["loop_seconds"] = loop;
        return json;
    }

    // Curate it (rename sounds, drop junk) and pass it back with --profile to
    // reuse its reach and names on later runs.
    private static JsonObject DraftProfile(RipResult rip, List<RipEntry> entries)
    {
        JsonArray Ids(params string[] kinds) => new(entries
            .Where(e => kinds.Contains(e.Sound.Kind) && !e.Suspect)
            .Select(e => (JsonNode)new JsonObject
            {
                ["name"] = e.Name ?? e.Sound.Id,
                ["id"] = e.Sound.Id,
                ["queue"] = $"0x{e.Sound.Address:X4}",
                ["value"] = e.Sound.Value,
                ["kind"] = e.Sound.Kind,
            }).ToArray());
        return new JsonObject
        {
            ["name"] = rip.Game,
            ["system"] = SystemNames[rip.System],
            ["author"] = rip.Author,
            ["date"] = rip.Date,
            ["notes"] = "Draft generated by SoulRipper; names and IDs not yet curated.",
            ["reach"] = JsonSerializer.SerializeToNode(rip.Reach.ToJson()),
            ["music"] = Ids("music", "jingle"),
            ["sfx"] = Ids("sfx"),
        };
    }
}

// What to write: the sounds discovery found, and about which game.
internal sealed record RipResult(string Game, string System, double FramesPerSecond, Reach Reach,
    IReadOnlyList<Sound> Sounds, string Author = "", string Date = "");
