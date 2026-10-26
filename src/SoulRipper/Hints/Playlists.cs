using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulRipper.Hints;

internal sealed record PlaylistTrack(int Track, string Title, double Seconds, bool Loops);

// The m3u playlists of a rip set, which name its tracks.
internal static class Playlists
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    // Sets have one m3u for the whole rip ("# @TITLE" header, plain titles) and
    // one per track ("Title - Composer - Game - (c) Date"); the former wins.
    public static List<PlaylistTrack> Parse(string folder)
    {
        var texts = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(f => File.ReadAllText(f, Latin1))
            .OrderBy(text => !text.Contains("@TITLE"))
            .ToList();

        var tracks = new SortedDictionary<int, PlaylistTrack>();
        foreach (var text in texts)
        {
            var whole = text.Contains("@TITLE");
            foreach (var line in text.Split('\n'))
            {
                var m = Regex.Match(line.Trim(), @"^[^:]*::(?:NSF|NSFE|GBS),(\d+),(.*)");
                if (!m.Success) continue;
                var track = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                // the title may contain commas; length, loop and fade come last
                var fields = m.Groups[2].Value.Split(',');
                if (tracks.ContainsKey(track) || fields.Length < 4) continue;
                var title = string.Join(",", fields[..^3]).Trim();
                if (!whole && CountOf(title, " - ") >= 3)
                    title = title[..NthLastIndexOf(title, " - ", 3)];
                tracks[track] = new PlaylistTrack(track, title, Seconds(fields[^3]), Seconds(fields[^1]) > 0);
            }
        }
        return tracks.Values.ToList();
    }

    // "1:02.5" -> 62.5; unreadable -> 0.
    private static double Seconds(string text)
    {
        var parts = text.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
        var total = 0.0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[^(i + 1)], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return 0;
            total += v * Math.Pow(60, i);
        }
        return total;
    }

    private static int CountOf(string text, string part) =>
        (text.Length - text.Replace(part, "").Length) / part.Length;

    private static int NthLastIndexOf(string text, string part, int n)
    {
        var at = text.Length;
        for (var i = 0; i < n; i++)
            at = text.LastIndexOf(part, at - 1, StringComparison.Ordinal);
        return at;
    }
}
