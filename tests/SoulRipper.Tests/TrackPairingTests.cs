using SoulRipper.Hints;

namespace SoulRipper.Tests;

public class TrackPairingTests
{
    private static readonly Sound[] Captured =
    {
        Sounds.Of(0xDFE8, 1, "music", aliases: new byte[] { 0x41 }, writes: new() { (0, 0xFF12, 0xF0), (1, 0xFF13, 0x83) }),
        Sounds.Of(0xDFE8, 2, "music", writes: new() { (0, 0xFF17, 0x77), (3, 0xFF18, 0x10), (5, 0xFF19, 0x86) }),
    };

    private static readonly PlaylistTrack[] Playlist = { new(0, "Overworld", 60, true), new(1, "Boss", 60, true) };

    private static PlayedRip Played(string system, params PlayedTrack[] tracks) => new(system, tracks);

    private static PlayedTrack Track(int track, (ushort, byte)[]? ram = null, (int, ushort, byte)[]? writes = null) =>
        new(track, ram ?? Array.Empty<(ushort, byte)>(), writes ?? Array.Empty<(int, ushort, byte)>());

    [Fact]
    public void A_track_storing_a_discovered_request_takes_its_name()
    {
        var played = Played("gb", Track(0, ram: new (ushort, byte)[] { (0xDFE8, 0x41) }));

        Assert.Equal(new Dictionary<string, string> { ["DFE8_01"] = "Overworld" }, TrackPairing.Pair(Captured, played, Playlist));
    }

    [Fact]
    public void Otherwise_tracks_pair_with_the_capture_they_sound_like()
    {
        var played = Played("gb", Track(1, writes: new (int, ushort, byte)[] { (9, 0xFF17, 0x77), (9, 0xFF18, 0x10), (9, 0xFF19, 0x86) }));

        Assert.Equal(new Dictionary<string, string> { ["DFE8_02"] = "Boss" }, TrackPairing.Pair(Captured, played, Playlist));
    }

    [Fact]
    public void Dissimilar_tracks_stay_unnamed()
    {
        var played = Played("gb", Track(1, writes: new (int, ushort, byte)[] { (0, 0xFF21, 0x01), (0, 0xFF22, 0x02) }));

        Assert.Empty(TrackPairing.Pair(Captured, played, Playlist));
    }

    [Fact]
    public void Bytes_init_sets_to_many_values_are_request_bytes()
    {
        var played = Played("gb", new byte[] { 1, 2, 3, 4 }
            .Select((v, i) => Track(i, ram: new (ushort, byte)[] { (0xDFE8, v), (0xDF00, 1) })).ToArray());

        Assert.Equal(new ushort[] { 0xDFE8 }, TrackPairing.RequestBytes(played));
    }

    [Fact]
    public void A_queue_used_by_only_some_tracks_still_counts()
    {
        var music = Enumerable.Range(1, 8).Select(v => Track(v, ram: new (ushort, byte)[] { (0x04F5, (byte)v) }));
        var effects = new byte[] { 1, 2, 4 }.Select(v => Track(100 + v, ram: new (ushort, byte)[] { (0x04F1, v) }));

        Assert.Equal(new ushort[] { 0x04F1, 0x04F5 }, TrackPairing.RequestBytes(Played("nes", music.Concat(effects).ToArray())));
    }

    [Fact]
    public void The_nes_stack_is_not_a_request_byte()
    {
        var played = Played("nes", new byte[] { 1, 2, 3 }
            .Select((v, i) => Track(i, ram: new (ushort, byte)[] { (0x01FB, v), (0x00FB, v) })).ToArray());

        Assert.Equal(new ushort[] { 0x00FB }, TrackPairing.RequestBytes(played));
    }
}
