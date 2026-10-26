using SoulRipper.Output;

namespace SoulRipper.Tests;

public class SuspectsTests
{
    private static IEnumerable<string> SuspectIds(IEnumerable<Sound> sounds) =>
        Suspects.Find(sounds).Select(s => s.Id).Order();

    [Fact]
    public void Values_past_a_long_silent_gap_are_suspect()
    {
        var sounds = new byte[] { 1, 2, 3, 5, 6, 0x25, 0x31 }.Select(v => Sounds.Of(0xA460, v));

        Assert.Equal(new[] { "A460_25", "A460_31" }, SuspectIds(sounds));
    }

    [Fact]
    public void Aliases_count_as_responding_values()
    {
        var aliases = Enumerable.Range(0, 10).Select(i => (byte)(2 + 4 * i));
        var sounds = new[] { Sounds.Of(0xA460, 1, aliases: aliases), Sounds.Of(0xA460, 0x28) };

        Assert.Empty(SuspectIds(sounds));
    }

    [Fact]
    public void Gaps_are_judged_per_queue()
    {
        var sounds = new[] { Sounds.Of(0xA460, 1), Sounds.Of(0xA470, 0x30) };

        Assert.Empty(SuspectIds(sounds));
    }

    [Fact]
    public void Multi_bit_values_on_bit_flag_queues_are_suspect()
    {
        var singles = Enumerable.Range(0, 8).Select(b =>
            Sounds.Of(0x04F1, (byte)(1 << b), aliases: b > 0 ? new[] { (byte)((1 << b) | 1) } : null));
        var combos = new[] { Sounds.Of(0x04F1, 0x41) };

        Assert.Equal(new[] { "04F1_41" }, SuspectIds(singles.Concat(combos)));
    }

    [Fact]
    public void A_queue_whose_aliases_ignore_bits_is_not_a_bit_flag_queue()
    {
        var sounds = Enumerable.Range(1, 18).Select(v =>
            Sounds.Of(0xDFE8, (byte)v, "music", aliases: v == 1 ? new byte[] { 0x23, 0x46, 0x5A, 0x6C, 0x96 } : null));

        Assert.Empty(SuspectIds(sounds));
    }

    [Fact]
    public void Values_a_community_rip_does_not_request_on_its_queue_are_suspect()
    {
        var sounds = new[] { Sounds.Of(0xDFE8, 1), Sounds.Of(0xDFE8, 2, aliases: new byte[] { 0x07 }), Sounds.Of(0xDFE8, 3), Sounds.Of(0xDFE0, 9) };
        var known = new Dictionary<ushort, HashSet<byte>> { [0xDFE8] = new() { 1, 7 } };

        Assert.Equal(new[] { "DFE8_03" }, Suspects.Find(sounds, known).Select(s => s.Id));
    }
}
