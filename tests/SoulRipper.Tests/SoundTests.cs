namespace SoulRipper.Tests;

public class SoundTests
{
    private static readonly List<(int, ushort, byte)> Writes = new() { (0, 0x4012, 0xA6), (0, 0x4015, 0x1F) };

    [Fact]
    public void Sounds_that_play_different_samples_are_not_aliases()
    {
        var kick = Sound.HashOf(null, Writes, new[] { (0, (ushort)0xE980, new byte[] { 0x55, 0xD5 }) });
        var snare = Sound.HashOf(null, Writes, new[] { (0, (ushort)0xE980, new byte[] { 0x12, 0x34 }) });

        Assert.NotEqual(kick, snare);
    }

    [Fact]
    public void Sounds_that_play_the_same_samples_are_aliases()
    {
        var first = Sound.HashOf(null, Writes, new[] { (0, (ushort)0xE980, new byte[] { 0x55, 0xD5 }) });
        var second = Sound.HashOf(null, Writes, new[] { (0, (ushort)0xE980, new byte[] { 0x55, 0xD5 }) });

        Assert.Equal(first, second);
    }
}
