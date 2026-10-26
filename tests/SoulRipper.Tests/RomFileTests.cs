using System.IO.Compression;

namespace SoulRipper.Tests;

public sealed class RomFileTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("soulripper-tests-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Zip(params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(dir, "game.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(data);
        }
        return path;
    }

    private static byte[] GameBoyRom(string title)
    {
        var rom = new byte[0x150];
        System.Text.Encoding.ASCII.GetBytes(title).CopyTo(rom, 0x134);
        return rom;
    }

    [Fact]
    public void The_single_rom_inside_a_zip_is_extracted_and_removed_afterwards()
    {
        var rom = GameBoyRom("TESTROM");
        var zip = Zip(("readme.txt", "ignored"u8.ToArray()), ("Game.gb", rom));

        string extracted;
        using (var file = RomFile.Open(zip))
        {
            extracted = file.Path;
            Assert.Equal(rom, File.ReadAllBytes(file.Path));
            Assert.Equal("gb", file.SystemId);
            Assert.Equal("Testrom", file.Title());
        }

        Assert.False(File.Exists(extracted));
    }

    [Fact]
    public void An_archive_without_a_rom_is_rejected()
    {
        var zip = Zip(("readme.txt", "ignored"u8.ToArray()));

        Assert.Throws<ArgumentException>(() => RomFile.Open(zip));
    }
}
