using System.IO.Compression;
using SoulRipper.Hints;
using SoulRipper.Output;

namespace SoulRipper.Tests;

public class VgmPackTests
{
    // A pulse-1 tune: (frame, address, value), one note per frame.
    private static List<(int, ushort, byte)> Tune(params int[] pitches) =>
        pitches.SelectMany((p, f) => new (int, ushort, byte)[] { (f, 0x4002, (byte)p), (f, 0x4003, (byte)(p >> 8)) }).ToList();

    private static VgmTrack Track(string title, List<(int, ushort, byte)> writes) =>
        new(title, writes.Select(w => (w.Item2, w.Item3)).ToList());

    [Fact]
    public void A_vgz_reads_back_its_title_and_writes()
    {
        var writes = new List<(int, ushort, byte)> { (0, 0x4002, 0xFD), (1, 0x4015, 0x1F) };
        var vgm = VgmWriter.Encode("nes", [], writes, frames: 2, loopStart: null, new Gd3("Overworld BGM", "Zelda", "NES"),
            samples: new List<(int, ushort, byte[])> { (1, 0xC000, new byte[] { 1, 2, 3 }) });
        using var vgz = new MemoryStream();
        using (var gzip = new GZipStream(vgz, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(vgm);

        var track = VgmFile.Read(vgz.ToArray(), "file name");

        Assert.Equal("Overworld BGM", track.Title);
        Assert.Equal(new (ushort, byte)[] { (0x4002, 0xFD), (0x4015, 0x1F), (0x4015, 0x00) }, track.Writes);
    }

    [Fact]
    public void Redundant_writes_do_not_change_the_melody()
    {
        var logged = Melody.Of(new (ushort, byte)[] { (0x4002, 0x10), (0x4002, 0x20), (0x4002, 0x30), (0x4002, 0x40) });
        var everyFrame = Melody.Of(new (ushort, byte)[]
        {
            (0x4000, 0xBF), (0x4002, 0x10), (0x4002, 0x10), (0x4000, 0xBF), (0x4002, 0x20), (0x4002, 0x20),
            (0x4002, 0x30), (0x4000, 0xBE), (0x4002, 0x40), (0x4003, 0x00),
        });

        Assert.Equal(1.0, logged.Similarity(everyFrame));
    }

    [Fact]
    public void Pack_tracks_name_the_captures_that_play_their_melody()
    {
        var sounds = new[]
        {
            Sounds.Of(0x0600, 0x01, "music", writes: Tune(0x1AB, 0x17C, 0x152, 0x13F, 0x11C, 0xFD)),
            Sounds.Of(0x0600, 0x40, "music", writes: Tune(0x0D5, 0x0BE, 0x0A9, 0x0A0, 0x08E)),
        };
        var pack = new[]
        {
            Track("Underworld BGM", Tune(0x0D5, 0x0D5, 0x0BE, 0x0A9, 0x0A0, 0x08E)),
            Track("Overworld BGM", Tune(0x1AB, 0x17C, 0x152, 0x13F, 0x11C, 0xFD)),
            Track("Ending Theme", Tune(0x300, 0x2A0, 0x280, 0x250, 0x200)),
        };

        var names = TrackPairing.PairByMelody(sounds, pack);

        Assert.Equal(new Dictionary<string, string> { ["0600_01"] = "Overworld BGM", ["0600_40"] = "Underworld BGM" }, names);
    }

    [Fact]
    public void A_pack_names_what_the_playlist_left_unnamed()
    {
        var sounds = new[]
        {
            Sounds.Of(0x0600, 0x01, "music", writes: Tune(0x1AB, 0x17C, 0x152, 0x13F)),
            Sounds.Of(0x0600, 0x02, "music", writes: Tune(0x0D5, 0x0BE, 0x0A9, 0x0A0)),
        };
        var played = new PlayedRip("nes", new[] { new PlayedTrack(0, new (ushort, byte)[] { (0x0600, 0x01) }, []) });
        var pack = new[] { Track("Title BGM", Tune(0x1AB, 0x17C, 0x152, 0x13F)), Track("Dungeon", Tune(0x0D5, 0x0BE, 0x0A9, 0x0A0)) };
        var hints = new GameHints("Game", new[] { new PlaylistTrack(0, "Title Screen", 60, true) }, played, pack);

        Assert.Equal(new Dictionary<string, string> { ["0600_01"] = "Title Screen", ["0600_02"] = "Dungeon" }, hints.Names(sounds));
    }

    [Fact]
    public void Packs_are_found_by_any_of_their_titles()
    {
        var packs = new VgmRips.Pack[]
        {
            new("The Legend of Zelda (UE) / The Hyrule Fantasy - Zelda no Densetsu (J)", "zelda.zip"),
            new("The Legend of Zelda 2 - Link no Bouken", "zelda2.zip"),
            new("Kirby's Dream Land (UE) / Hoshi no Kirby (J)", "kirby.zip"),
        };

        Assert.Equal(new[] { packs[0] }, VgmRips.Find("Legend of Zelda, The (USA) (Rev 1)", packs));
        Assert.Equal(new[] { packs[2] }, VgmRips.Find("Hoshi no Kirby (Japan)", packs));
        Assert.Empty(VgmRips.Find("Legend of Zelda, The - Link's Awakening (USA, Europe)", packs));
    }

    [Fact]
    public void Packs_are_listed_under_the_title_as_written_or_without_its_article()
    {
        Assert.Equal(new[] { "T", "L" }, VgmRips.Letters("Legend of Zelda, The (USA) (Rev 1)"));
        Assert.Equal(new[] { "#" }, VgmRips.Letters("1942 (Japan, USA)"));
    }

    [Fact]
    public void A_listing_yields_each_pack_title_and_download()
    {
        const string html = """
            <div class="result row" id="pack-1290">
              <h2 class="clearfix title">
                <a href="https://vgmrips.net/packs/pack/kirby#autoplay"><span class="icon-play"> </span></a>
                <a href="https://vgmrips.net/packs/pack/kirby">Kirby&#039;s Dream Land (UE)</a>
                <a class="download" href="https://vgmrips.net/files/GameBoy/Kirby%27s_Dream_Land.zip"><small>15 songs</small></a>
              </h2>
            </div>
            """;

        Assert.Equal(new[] { new VgmRips.Pack("Kirby's Dream Land (UE)", "https://vgmrips.net/files/GameBoy/Kirby%27s_Dream_Land.zip") },
            VgmRips.ParseListing(html));
    }
}
