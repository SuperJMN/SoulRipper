namespace SoulRipper;

// What playing each track of a community rip (NSF/GBS) did: the RAM its INIT
// wrote and the sound registers it wrote, per frame.
internal sealed record PlayedTrack(
    int Track,
    IReadOnlyList<(ushort Address, byte Value)> Ram,
    IReadOnlyList<(int Frame, ushort Address, byte Value)> Writes);

internal sealed record PlayedRip(string System, IReadOnlyList<PlayedTrack> Tracks)
{
    public static PlayedRip FromFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gbs" => GbsPlayer.Play(File.ReadAllBytes(path)),
            ".nsf" => NsfPlayer.Play(File.ReadAllBytes(path)),
            var ext => throw new ArgumentException($"unsupported rip type: {ext}"),
        };
}
