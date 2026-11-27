using System.IO.Hashing;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulRipper.Hints;

// What the online sources say about a game:
//  1. its name, from the ROM's CRC32 in the No-Intro DATs (libretro-database);
//  2. its NSF/GBS set on joshw.info, whose playlists name every track;
//  3. playing that rip: its INIT stores the game's own request byte, which
//     tells discovery where to look and pairs each track with a capture;
//  4. its VGM pack on vgmrips.net, whose tags name the tracks the set's
//     playlists miss (or the set itself, when it has none).
// Downloads are cached.
internal sealed class GameHints
{
    private const string DatBase = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/";
    private const string UserAgent = "soulripper (https://github.com/SuperJMN/SoulRipper)";

    private static readonly Dictionary<string, string[]> Dats = new()
    {
        ["nes"] = new[] { "Nintendo - Nintendo Entertainment System.dat" },
        ["gb"] = new[] { "Nintendo - Game Boy.dat", "Nintendo - Game Boy Color.dat" },
    };
    private static readonly Dictionary<string, string> RipSites = new()
    {
        ["nes"] = "https://nsf.joshw.info/",
        ["gb"] = "https://gbs.joshw.info/",
    };
    private static readonly Dictionary<string, string[]> RipExtensions = new()
    {
        ["nes"] = new[] { ".nsf" },
        ["gb"] = new[] { ".gbs" },
    };

    public static readonly GameHints None = new(null, Array.Empty<PlaylistTrack>(), null, Array.Empty<VgmTrack>());

    public GameHints(string? game, IReadOnlyList<PlaylistTrack> playlist, PlayedRip? played, IReadOnlyList<VgmTrack> pack)
    {
        Game = game;
        Playlist = playlist;
        Played = played;
        Pack = pack;
    }

    public string? Game { get; }
    public IReadOnlyList<PlaylistTrack> Playlist { get; }
    public PlayedRip? Played { get; }
    public IReadOnlyList<VgmTrack> Pack { get; }

    public IReadOnlyList<ushort> Queues => Played is null ? Array.Empty<ushort>() : TrackPairing.RequestBytes(Played);

    // Request values the rip makes on each of its request bytes.
    public IReadOnlyDictionary<ushort, HashSet<byte>> KnownRequests =>
        Played is null ? new Dictionary<ushort, HashSet<byte>>() : TrackPairing.KnownRequests(Played);

    // The set's playlist names first (its tracks are paired by the request
    // they store); the VGM pack names what it left unnamed.
    public Dictionary<string, string> Names(IReadOnlyList<Sound> sounds)
    {
        var names = Played is null || Playlist.Count == 0 ? new() : TrackPairing.Pair(sounds, Played, Playlist);
        var unnamed = sounds.Where(s => !s.Aliases.Prepend(s.Id).Any(names.ContainsKey)).ToList();
        foreach (var (id, title) in TrackPairing.PairByMelody(unnamed, Pack))
            names.TryAdd(id, title);
        return names;
    }

    public static string CacheDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg
            : OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "soulripper");

    // Hints for a ROM; empty when offline or nothing is found.
    public static GameHints Lookup(string rom, string system, Action<string> log)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            var game = Identify(http, rom, system);
            if (game is null)
            {
                log("hints: ROM not found in the No-Intro DATs");
                return None;
            }
            log($"hints: identified as {game}");

            var pack = PackOf(http, game, system, log);
            var url = FindRip(http, game, system);
            if (url is null)
            {
                log("hints: no NSF/GBS set found on joshw.info");
                return new GameHints(game, Array.Empty<PlaylistTrack>(), null, pack);
            }
            var folder = FetchRip(http, url);
            var rip = Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => RipExtensions[system].Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (rip is null)
                return new GameHints(game, Array.Empty<PlaylistTrack>(), null, pack);

            var played = PlayedRip.FromFile(rip);
            var playlist = Playlists.Parse(folder);
            log($"hints: {Path.GetFileName(folder)}: {played.Tracks.Count} tracks, {playlist.Count} named");
            return new GameHints(game, playlist, played, pack);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       or InvalidOperationException or ArgumentException)
        {
            log($"hints unavailable: {ex.Message}");
            return None;
        }
    }

    // The game's VGM pack tracks; none when the site fails, so the other
    // hints still count.
    private static IReadOnlyList<VgmTrack> PackOf(HttpClient http, string game, string system, Action<string> log)
    {
        try
        {
            return VgmRips.Lookup(http, game, system, log);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            log($"hints: vgmrips.net unavailable: {ex.Message}");
            return Array.Empty<VgmTrack>();
        }
    }

    // CRC32s the DATs may list: whole file and, for NES, without the header.
    public static HashSet<string> RomCrcs(byte[] data)
    {
        static string Crc(ReadOnlySpan<byte> bytes) => Crc32.HashToUInt32(bytes).ToString("X8");
        var crcs = new HashSet<string> { Crc(data) };
        if (data.Length > 16 && data.AsSpan(0, 4).SequenceEqual("NES\u001a"u8))
            crcs.Add(Crc(data.AsSpan(16)));
        return crcs;
    }

    // No-Intro name of the ROM, or null.
    private static string? Identify(HttpClient http, string rom, string system)
    {
        var crcs = RomCrcs(File.ReadAllBytes(rom));
        foreach (var dat in Dats[system])
        {
            var path = Get(http, DatBase + Uri.EscapeDataString(dat), Path.Combine(CacheDir, "dat", dat));
            string? name = null;
            foreach (var line in File.ReadLines(path, Encoding.Latin1))
            {
                var m = Regex.Match(line, "^\\s*name \"(.*)\"");
                if (m.Success) name = m.Groups[1].Value;
                m = Regex.Match(line, @"\bcrc ([0-9A-Fa-f]{8})\b");
                if (m.Success && name is not null && crcs.Contains(m.Groups[1].Value.ToUpperInvariant()))
                    return name;
            }
        }
        return null;
    }

    // URL of the best-matching set on joshw.info, or null. Checks the game's
    // own initial and the initials of other title words first (e.g. "Mr. Gimmick"
    // -> "m/", "g/" for "Gimmick! EUR [Mr. Gimmick]"); if none match, scans the
    // remaining index letters and picks the global best match so alternative
    // regional titles in brackets are found without letting an earlier 0.91 hit
    // beat a later 1.0 exact match.
    private static string? FindRip(HttpClient http, string game, string system)
    {
        var target = Titles.Normalize(game);
        if (target.Length == 0)
            return null;
        var preferredRegion = RegionTag(game);
        var (priority, fallback) = SearchLetters(game);

        string? Search(IEnumerable<string> letters)
        {
            (string Url, double Score, bool RegionMatch)? best = null;
            foreach (var letter in letters)
            {
                var index = RipSites[system] + letter + "/";
                var path = Path.Combine(CacheDir, "index", system, letter + ".html");
                string html;
                try
                {
                    html = File.ReadAllText(Get(http, index, path), Encoding.Latin1);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "");
                    continue;
                }
                if (BestRipIn(html, target, preferredRegion) is { } hit
                    && (best is null || hit.Score > best.Value.Score
                        || hit.Score == best.Value.Score && hit.RegionMatch && !best.Value.RegionMatch))
                {
                    best = (index + hit.Href, hit.Score, hit.RegionMatch);
                    if (best.Value.Score >= 1.0 && (preferredRegion is null || best.Value.RegionMatch))
                        return best.Value.Url;
                }
            }
            return best?.Url;
        }

        return Search(priority) ?? Search(fallback);
    }

    internal static (List<string> Priority, List<string> Fallback) SearchLetters(string game)
    {
        static string Bucket(char c) => char.IsAsciiLetter(c) ? char.ToLowerInvariant(c).ToString() : "0-9";
        var priority = new List<string>();
        void AddPriority(string b) { if (!priority.Contains(b)) priority.Add(b); }

        var target = Titles.Normalize(game);
        if (target.Length > 0) AddPriority(Bucket(target[0]));
        foreach (Match word in Regex.Matches(Titles.DisplayName(game), "[A-Za-z0-9]+"))
            AddPriority(Bucket(word.Value[0]));

        var fallback = Enumerable.Range('a', 26).Select(c => ((char)c).ToString()).Append("0-9")
            .Where(b => !priority.Contains(b)).ToList();
        return (priority, fallback);
    }

    internal static (string Href, double Score, bool RegionMatch)? BestRipIn(
        string html, string normalizedTarget, string? preferredRegion = null)
    {
        string? best = null;
        var bestScore = 0.0;
        var bestRegionMatch = false;
        foreach (Match m in Regex.Matches(html, "href=\"([^\"?/][^\"]*\\.7z)\""))
        {
            var href = m.Groups[1].Value;
            var decoded = Uri.UnescapeDataString(href);
            var regionMatch = preferredRegion is not null
                && Regex.IsMatch(decoded, $@"\b{preferredRegion}\b", RegexOptions.IgnoreCase);
            foreach (var title in Titles.RipTitles(href))
            {
                var score = SequenceMatcher.Ratio(normalizedTarget.ToCharArray(), Titles.Normalize(title).ToCharArray());
                if (score > bestScore || score == bestScore && regionMatch && !bestRegionMatch)
                    (best, bestScore, bestRegionMatch) = (href, score, regionMatch);
            }
        }
        return best is not null && bestScore >= 0.9 ? (best, bestScore, bestRegionMatch) : null;
    }

    private static string? RegionTag(string noIntroName)
    {
        if (noIntroName.Contains("(Europe)", StringComparison.OrdinalIgnoreCase)) return "EUR";
        if (noIntroName.Contains("(Japan)", StringComparison.OrdinalIgnoreCase)) return "JPN";
        if (noIntroName.Contains("(USA)", StringComparison.OrdinalIgnoreCase)) return "USA";
        foreach (Match m in Regex.Matches(noIntroName, @"\(([^)]+)\)"))
        {
            var tag = m.Groups[1].Value;
            if (Regex.IsMatch(tag, @"\bEurope\b", RegexOptions.IgnoreCase)) return "EUR";
            if (Regex.IsMatch(tag, @"\bJapan\b", RegexOptions.IgnoreCase)) return "JPN";
            if (Regex.IsMatch(tag, @"\bUSA\b", RegexOptions.IgnoreCase)) return "USA";
        }
        return null;
    }

    // Downloads and unpacks a set; returns its directory.
    private static string FetchRip(HttpClient http, string url)
    {
        var name = Path.GetFileName(Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]));
        var archive = Get(http, url, Path.Combine(CacheDir, "rips", name));
        var folder = archive[..^Path.GetExtension(archive).Length];
        if (!Directory.Exists(folder))
        {
            Archives.ExtractFlat(archive, folder + ".part");
            Directory.Move(folder + ".part", folder);
        }
        return folder;
    }

    internal static string Get(HttpClient http, string url, string path)
    {
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var response = http.GetAsync(url).GetAwaiter().GetResult())
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"{url}: {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
            using var file = File.Create(path + ".part");
            response.Content.CopyTo(file, null, CancellationToken.None);
        }
        File.Move(path + ".part", path, overwrite: true);
        return path;
    }
}
