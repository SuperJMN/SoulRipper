namespace SoulRipper.Tests;

internal static class Sounds
{
    public static Sound Of(ushort queue, byte value, string kind = "sfx", IEnumerable<byte>? aliases = null,
        int frames = 60, int? loopStart = null, List<(int, ushort, byte)>? writes = null)
    {
        var sound = new Sound(queue, value, kind, loopStart is null ? "ended" : "loop", frames, loopStart,
            frames - loopStart, new[] { 0, 1 }, (ulong)(queue << 8 | value),
            new List<(ushort, byte)> { (0xFF26, 0x80) },
            writes ?? new List<(int, ushort, byte)> { (0, 0xFF12, 0xF0), (0, 0xFF14, 0x87) });
        sound.Aliases.AddRange((aliases ?? Array.Empty<byte>()).Select(a => $"{queue:X4}_{a:X2}"));
        return sound;
    }
}
