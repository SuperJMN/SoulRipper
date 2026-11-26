using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulRipper.Hints;

// VGM packs on vgmrips.net: logs of a game's music and jingles, each titled
// in its GD3 tag. They name tracks when the NSF/GBS set has no playlist (the
// Zelda NSF) and are paired with captures by melody, since they come from
// other players.
internal static class VgmRips
{
    private const string Site = "https://vgmrips.net/packs/chip/";
    private const int PageSize = 20;
    private const int MaxPacks = 3;

    // The pack listings by chip cover every system that uses it (NES, Famicom,
    // Vs.; Game Boy and Color).
    private static readonly Dictionary<string, string> Chips = new() { ["nes"] = "nes-apu", ["gb"] = "game-boy-dmg" };

    internal sealed record Pack(string Title, string Download);

    // Tracks of the packs whose title matches the No-Intro name; several when
    // they tie (Tetris has a Nintendo and a Tengen pack), and the melodies pick.
    public static List<VgmTrack> Lookup(HttpClient http, string game, string system, Action<string> log)
    {
        var packs = Find(game, Letters(game).SelectMany(l => Listing(http, system, l)).DistinctBy(p => p.Download));
        if (packs.Count == 0)
        {
            log("hints: no VGM pack found on vgmrips.net");
            return new List<VgmTrack>();
        }

        var tracks = new List<VgmTrack>();
        foreach (var pack in packs)
        {
            var folder = Fetch(http, pack.Download);
            var read = Directory.EnumerateFiles(folder)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".vgz" or ".vgm")
                .Order(StringComparer.Ordinal)
                .Select(f => VgmFile.Read(File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f)))
                .ToList();
            log($"hints: vgmrips.net: {pack.Title}: {read.Count} tracks");
            tracks.AddRange(read);
        }
        return tracks;
    }

    // Packs whose main or alternative title ("A / B") best matches the game.
    public static List<Pack> Find(string game, IEnumerable<Pack> packs)
    {
        var target = Titles.Normalize(game);
        var scored = packs
            .Select(p => (Pack: p, Score: p.Title.Split('/')
                .Select(t => SequenceMatcher.Ratio(target.ToCharArray(), Titles.Normalize(t).ToCharArray())).Max()))
            .Where(x => x.Score >= 0.9)
            .ToList();
        if (scored.Count == 0)
            return new List<Pack>();
        var best = scored.Max(x => x.Score);
        return scored.Where(x => x.Score == best).Select(x => x.Pack).Take(MaxPacks).ToList();
    }

    // Listing letters that may hold the game: vgmrips files packs by their
    // title as written, "The Legend of Zelda" under T, while No-Intro writes
    // "Legend of Zelda, The".
    public static List<string> Letters(string game)
    {
        static string LetterOf(string title) =>
            title.Length > 0 && char.IsAsciiLetter(title[0]) ? char.ToUpperInvariant(title[0]).ToString() : "#";
        return new[] { LetterOf(Titles.DisplayName(game)), LetterOf(game.TrimStart()) }.Distinct().ToList();
    }

    // Every pack listed under a letter, page by page.
    private static List<Pack> Listing(HttpClient http, string system, string letter)
    {
        var packs = new List<Pack>();
        for (var page = 0; ; page++)
        {
            var url = $"{Site}{Chips[system]}?letter={Uri.EscapeDataString(letter)}" + (page > 0 ? $"&p={page}" : "");
            var name = (letter == "#" ? "0-9" : letter) + (page > 0 ? $"-{page}" : "") + ".html";
            var html = File.ReadAllText(GameHints.Get(http, url, Path.Combine(GameHints.CacheDir, "index", "vgmrips", system, name)), Encoding.UTF8);
            packs.AddRange(ParseListing(html));
            var total = Regex.Match(html, @"of (\d+) total") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
            if ((page + 1) * PageSize >= total)
                return packs;
        }
    }

    public static IEnumerable<Pack> ParseListing(string html)
    {
        foreach (var block in html.Split("<div class=\"result row\"").Skip(1))
        {
            var title = Regex.Match(block, "<a href=\"[^\"#]*/packs/pack/[^\"#]+\">([^<]+)</a>");
            var download = Regex.Match(block, "class=\"download\" href=\"([^\"]+\\.zip)\"");
            if (title.Success && download.Success)
                yield return new Pack(WebUtility.HtmlDecode(title.Groups[1].Value).Trim(), WebUtility.HtmlDecode(download.Groups[1].Value));
        }
    }

    private static string Fetch(HttpClient http, string url)
    {
        var name = Path.GetFileName(Uri.UnescapeDataString(new Uri(url).AbsolutePath));
        var archive = GameHints.Get(http, url, Path.Combine(GameHints.CacheDir, "vgmrips", name));
        var folder = archive[..^Path.GetExtension(archive).Length];
        if (!Directory.Exists(folder))
        {
            Archives.ExtractFlat(archive, folder + ".part");
            Directory.Move(folder + ".part", folder);
        }
        return folder;
    }
}
