using System.Buffers.Binary;
using System.IO.Compression;
using SoulRipper.Output;

namespace SoulRipper.Tests;

public sealed class RipWriterTests : IDisposable
{
    private readonly string outDir = Directory.CreateTempSubdirectory("soul-ripper-tests-").FullName;

    public void Dispose() => Directory.Delete(outDir, recursive: true);

    private static RipResult Rip(params Sound[] sounds) => new("Test", "gb", 59.7275, Reach.Default, sounds);

    [Fact]
    public void Sounds_are_written_by_kind_with_their_names()
    {
        var entries = RipWriter.Write(
            Rip(Sounds.Of(0xA468, 1, "music", frames: 600, loopStart: 100), Sounds.Of(0xA460, 5)),
            outDir, new Dictionary<string, string> { ["A460_05"] = "Coin" }, keepSuspect: false);

        Assert.Equal(new[] { "music/01 - Music 01.vgz", "sfx/01 - Coin.vgz" }, entries.Select(e => e.File).Order());
        Assert.True(File.Exists(Path.Combine(outDir, "sfx", "01 - Coin.vgz")));
        Assert.True(File.Exists(Path.Combine(outDir, "profile.json")));
    }

    [Fact]
    public void A_name_given_to_an_alias_names_the_sound()
    {
        var entries = RipWriter.Write(Rip(Sounds.Of(0xA460, 5, aliases: new byte[] { 0x45 })),
            outDir, new Dictionary<string, string> { ["A460_45"] = "Coin" }, keepSuspect: false);

        Assert.Equal("Coin", entries.Single().Name);
    }

    [Fact]
    public void Looping_music_gets_a_vgm_loop_point()
    {
        RipWriter.Write(Rip(Sounds.Of(0xA468, 1, "music", frames: 600, loopStart: 100)),
            outDir, new Dictionary<string, string>(), keepSuspect: false);

        var header = Gunzip(Path.Combine(outDir, "music", "01 - Music 01.vgz")).AsSpan();
        var total = BinaryPrimitives.ReadInt32LittleEndian(header[0x18..]);
        var loopOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x1C..]);
        var loopSamples = BinaryPrimitives.ReadInt32LittleEndian(header[0x20..]);
        Assert.True(loopOffset > 0);
        Assert.Equal(500.0 / 600, (double)loopSamples / total, 2);
    }

    [Fact]
    public void Uncompressed_output_writes_plain_vgm()
    {
        var entries = RipWriter.Write(Rip(Sounds.Of(0xA460, 5)), outDir, new Dictionary<string, string>(),
            keepSuspect: false, compress: false);

        var bytes = File.ReadAllBytes(Path.Combine(outDir, entries.Single().File!));
        Assert.Equal("Vgm "u8.ToArray(), bytes[..4]);
    }

    private static byte[] Gunzip(string path)
    {
        using var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void Suspects_are_skipped_unless_kept()
    {
        var sounds = new byte[] { 1, 2, 0x30 }.Select(v => Sounds.Of(0xA460, v)).ToArray();

        var entries = RipWriter.Write(Rip(sounds), outDir, new Dictionary<string, string>(), keepSuspect: false);

        Assert.Equal(new[] { "A460_30" }, entries.Where(e => e.File is null).Select(e => e.Sound.Id));
    }
}
