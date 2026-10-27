namespace SoulRipper.Tests;

public class SampleMemoryTests
{
    private static readonly byte[] Kick = { 0x55, 0xD5, 0xAA };
    private static readonly byte[] Snare = { 0x12, 0x34, 0x56 };

    private static IEnumerable<string> Loads(int frames, int? loopStart, params (int, ushort, byte[])[] played) =>
        SampleMemory.Blocks(played, frames, loopStart)
            .Select(b => $"{b.Frame}:{b.Address:X4}:{Convert.ToHexString(b.Data)}");

    [Fact]
    public void A_sample_played_again_is_loaded_once()
    {
        var loads = Loads(100, null, (10, 0xE980, Kick), (40, 0xE980, Kick));

        Assert.Equal(new[] { "10:E980:55D5AA" }, loads);
    }

    [Fact]
    public void A_sample_is_loaded_again_when_other_bytes_took_its_memory()
    {
        // MMC3 games such as SMB3 bank-switch other samples into the same addresses.
        var loads = Loads(100, null, (10, 0xE980, Kick), (20, 0xE980, Snare), (30, 0xE980, Kick));

        Assert.Equal(new[] { "10:E980:55D5AA", "20:E980:123456", "30:E980:55D5AA" }, loads);
    }

    [Fact]
    public void A_shorter_sample_inside_a_loaded_one_needs_no_load()
    {
        var loads = Loads(100, null, (10, 0xF280, Kick), (20, 0xF280, Kick[..2]), (30, 0xF281, Kick[1..]));

        Assert.Equal(new[] { "10:F280:55D5AA" }, loads);
    }

    [Fact]
    public void The_loop_loads_its_samples_again()
    {
        // On later passes the RAM holds what the end of the loop left, not
        // what was loaded before it.
        var loads = Loads(100, 50, (10, 0xE980, Kick), (60, 0xE980, Kick), (70, 0xE980, Snare), (80, 0xE980, Kick));

        Assert.Equal(new[] { "10:E980:55D5AA", "60:E980:55D5AA", "70:E980:123456", "80:E980:55D5AA" }, loads);
    }

    [Fact]
    public void Samples_started_after_the_sound_ends_are_left_out()
    {
        var loads = Loads(100, null, (10, 0xE980, Kick), (100, 0xE000, Snare));

        Assert.Equal(new[] { "10:E980:55D5AA" }, loads);
    }

    [Fact]
    public void A_sample_running_past_FFFF_continues_at_8000()
    {
        var ranges = SampleMemory.Ranges(0xFFC0, 0x81);

        Assert.Equal(new (ushort, int)[] { (0xFFC0, 0x40), (0x8000, 0x41) }, ranges);
    }

    [Fact]
    public void A_sample_within_the_bank_is_one_range()
    {
        var ranges = SampleMemory.Ranges(0xE980, 257);

        Assert.Equal(new (ushort, int)[] { (0xE980, 257) }, ranges);
    }
}
