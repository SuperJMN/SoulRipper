using System.IO.Hashing;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulRipper.Hints;

// What the online sources say about a game:
//  1. its name, from the ROM's CRC32 in the No-Intro DATs (libretro-database);
//  2. its NSF/GBS set on joshw.info, whose playlists name every track;
//  3. playing that rip: its INIT stores the game's own request byte, which
//     tells discovery where to look and pairs each track with a capture.
// Downloads are cached.
internal sealed class GameHints
{
    private const string DatBase = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro/";
    private const string UserAgent = "soul-ripper (https://github.com/SuperJMN/SoulRipper)";

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

    public static readonly GameHints None = new(null, Array.Empty<PlaylistTrack>(), null);

    public GameHints(string? game, IReadOnlyList<PlaylistTrack> playlist, PlayedRip? played)
    {
        Game = game;
        Playlist = playlist;
        Played = played;
    }

    public string? Game { get; }
    public IReadOnlyList<PlaylistTrack> Playlist { get; }
    public PlayedRip? Played { get; }

    public IReadOnlyList<ushort> Queues => Played is null ? Array.Empty<ushort>() : TrackPairing.RequestBytes(Played);

    // Request values the rip makes on each of its request bytes.
    public IReadOnlyDictionary<ushort, HashSet<byte>> KnownRequests =>
        Played is null ? new Dictionary<ushort, HashSet<byte>>() : TrackPairing.KnownRequests(Played);

    public Dictionary<string, string> Names(IReadOnlyList<Sound> sounds) =>
        Played is null || Playlist.Count == 0 ? new() : TrackPairing.Pair(sounds, Played, Playlist);

    public static string CacheDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg
            : OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "soul-ripper");

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

            var url = FindRip(http, game, system);
            if (url is null)
            {
                log("hints: no NSF/GBS set found on joshw.info");
                return new GameHints(game, Array.Empty<PlaylistTrack>(), null);
            }
            var folder = FetchRip(http, url);
            var rip = Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => RipExtensions[system].Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (rip is null)
                return new GameHints(game, Array.Empty<PlaylistTrack>(), null);

            var played = PlayedRip.FromFile(rip);
            var playlist = Playlists.Parse(folder);
            log($"hints: {Path.GetFileName(folder)}: {played.Tracks.Count} tracks, {playlist.Count} named");
            return new GameHints(game, playlist, played);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       or InvalidOperationException or ArgumentException)
        {
            log($"hints unavailable: {ex.Message}");
            return None;
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

    // URL of the best-matching set on joshw.info, or null.
    private static string? FindRip(HttpClient http, string game, string system)
    {
        var target = Titles.Normalize(game);
        if (target.Length == 0)
            return null;
        var letter = char.IsAsciiLetter(target[0]) ? target[..1] : "0-9";
        var index = RipSites[system] + letter + "/";
        var html = File.ReadAllText(Get(http, index, Path.Combine(CacheDir, "index", system, letter + ".html")), Encoding.Latin1);

        string? best = null;
        var bestScore = 0.0;
        foreach (Match m in Regex.Matches(html, "href=\"([^\"?/][^\"]*\\.7z)\""))
            foreach (var title in Titles.RipTitles(m.Groups[1].Value))
            {
                var score = SequenceMatcher.Ratio(target.ToCharArray(), Titles.Normalize(title).ToCharArray());
                if (score > bestScore)
                    (best, bestScore) = (m.Groups[1].Value, score);
            }
        return best is not null && bestScore >= 0.9 ? index + best : null;
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

    private static string Get(HttpClient http, string url, string path)
    {
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var response = http.GetAsync(url).GetAwaiter().GetResult())
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"{url}: {(int)response.StatusCode} {response.ReasonPhrase}");
            using var file = File.Create(path + ".part");
            response.Content.CopyTo(file, null, CancellationToken.None);
        }
        File.Move(path + ".part", path, overwrite: true);
        return path;
    }
}
