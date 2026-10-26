using System.Globalization;

namespace SoulRipper;

// Command line: positional arguments plus `--key value` / `--flag` options.
internal sealed class Options
{
    private readonly Dictionary<string, string> values;

    private Options(List<string> positional, Dictionary<string, string> values)
    {
        Positional = positional;
        this.values = values;
    }

    public IReadOnlyList<string> Positional { get; }

    public static Options Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        var positional = new List<string>();
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--"))
            {
                positional.Add(list[i]);
                continue;
            }
            var key = list[i][2..];
            d[key] = i + 1 < list.Count && !list[i + 1].StartsWith("--") && !IsFlag(key) ? list[++i] : "true";
        }
        return new Options(positional, d);
    }

    private static bool IsFlag(string key) => key is "offline" or "keep-suspect" or "no-rebase" or "vgm" or "help";

    public string Req(string key) =>
        values.TryGetValue(key, out var v) ? v : throw new ArgumentException($"missing --{key}");

    public string? Get(string key) => values.GetValueOrDefault(key);

    public bool Flag(string key) => values.ContainsKey(key);

    public int Int(string key, int fallback) =>
        values.TryGetValue(key, out var v) ? ParseNumber(v) : fallback;

    // Accepts decimal or 0x-prefixed hex.
    public static int ParseNumber(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(text[2..], NumberStyles.HexNumber)
            : int.Parse(text, CultureInfo.InvariantCulture);

    // "A460,0xA468" -> addresses (hex).
    public static List<ushort>? Addresses(string? list) =>
        list?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(q => (ushort)ParseNumber("0x" + q.Replace("0x", "", StringComparison.OrdinalIgnoreCase))).ToList();
}
