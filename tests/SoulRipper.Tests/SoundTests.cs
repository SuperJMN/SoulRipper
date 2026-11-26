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

    [Fact]
    public void A_sound_that_starts_a_frame_later_is_an_alias()
    {
        var now = Sound.HashOf(4, new List<(int, ushort, byte)> { (0, 0xFF12, 0xF0), (2, 0xFF14, 0x87) }, []);
        var later = Sound.HashOf(5, new List<(int, ushort, byte)> { (1, 0xFF12, 0xF0), (3, 0xFF14, 0x87) }, []);

        Assert.Equal(now, later);
    }

    [Fact]
    public void Sounds_whose_writes_are_spaced_differently_are_not_aliases()
    {
        var fast = Sound.HashOf(null, new List<(int, ushort, byte)> { (0, 0xFF12, 0xF0), (1, 0xFF14, 0x87) }, []);
        var slow = Sound.HashOf(null, new List<(int, ushort, byte)> { (0, 0xFF12, 0xF0), (2, 0xFF14, 0x87) }, []);

        Assert.NotEqual(fast, slow);
    }

    [Fact]
    public void Frames_that_only_repeat_the_idle_writes_do_not_delay_a_jingle()
    {
        var now = Sound.HashOf(null, new List<(int, ushort, byte)>
            { (0, 0xFF24, 0x77), (0, 0xFF12, 0xF0), (1, 0xFF24, 0x77), (2, 0xFF24, 0x77) }, []);
        var later = Sound.HashOf(null, new List<(int, ushort, byte)>
            { (0, 0xFF24, 0x77), (1, 0xFF24, 0x77), (1, 0xFF12, 0xF0), (2, 0xFF24, 0x77), (3, 0xFF24, 0x77) }, []);

        Assert.Equal(now, later);
    }
}
