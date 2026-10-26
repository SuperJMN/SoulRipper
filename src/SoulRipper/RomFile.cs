using System.Globalization;
using SharpCompress.Archives;
using IOPath = System.IO.Path;

namespace SoulRipper;

// A ROM ready to emulate: the file itself, or the single ROM inside an
// archive (zip, 7z...) extracted to a temporary file.
internal sealed class RomFile : IDisposable
{
    public static readonly string[] Extensions = { ".nes", ".gb", ".gbc" };

    private readonly bool temporary;

    private RomFile(string path, string name, bool temporary)
    {
        Path = path;
        Name = name;
        this.temporary = temporary;
    }

    public string Path { get; }

    // File name of the ROM itself (inside the archive, if any).
    public string Name { get; }

    public string SystemId => Name.EndsWith(".nes", StringComparison.OrdinalIgnoreCase) ? "nes" : "gb";

    public static RomFile Open(string path)
    {
        var full = IOPath.GetFullPath(path);
        if (Extensions.Contains(IOPath.GetExtension(full).ToLowerInvariant()))
            return new RomFile(full, IOPath.GetFileName(full), false);

        using var archive = ArchiveFactory.OpenArchive(full);
        var roms = archive.Entries
            .Where(e => !e.IsDirectory && Extensions.Contains(IOPath.GetExtension(e.Key ?? "").ToLowerInvariant()))
            .ToList();
        if (roms.Count == 0)
            throw new ArgumentException($"no supported ROM ({string.Join(", ", Extensions)}) in {path}");
        if (roms.Count > 1)
            throw new ArgumentException($"several ROMs in {path}; extract the one to rip: {string.Join(", ", roms.Select(e => e.Key))}");

        var name = IOPath.GetFileName(roms[0].Key!);
        var temp = IOPath.Combine(IOPath.GetTempPath(), $"soul-ripper-{Guid.NewGuid():N}{IOPath.GetExtension(name)}");
        using (var input = roms[0].OpenEntryStream())
        using (var output = File.Create(temp))
            input.CopyTo(output);
        return new RomFile(temp, name, true);
    }

    // Internal title for Game Boy ROMs, file name otherwise.
    public string Title()
    {
        if (SystemId == "gb")
        {
            using var f = File.OpenRead(Path);
            var raw = new byte[16];
            f.Seek(0x134, SeekOrigin.Begin);
            var n = f.Read(raw);
            var title = new string(raw.Take(n).TakeWhile(b => b != 0).Select(b => b is >= 0x20 and < 0x7F ? (char)b : '?').ToArray()).Trim();
            if (title.Length > 0)
                return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title.ToLowerInvariant());
        }
        return IOPath.GetFileNameWithoutExtension(Name);
    }

    public void Dispose()
    {
        if (temporary) File.Delete(Path);
    }
}
