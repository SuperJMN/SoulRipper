using SharpCompress.Archives;

namespace SoulRipper.Hints;

internal static class Archives
{
    // Extracts an archive (7z, zip, rar...) flat into a directory: entry paths
    // are dropped so a hostile archive cannot write outside it.
    public static void ExtractFlat(string archivePath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            var name = Path.GetFileName(entry.Key ?? "");
            if (name.Length == 0) continue;
            using var input = entry.OpenEntryStream();
            using var output = File.Create(Path.Combine(outDir, name));
            input.CopyTo(output);
        }
    }
}
