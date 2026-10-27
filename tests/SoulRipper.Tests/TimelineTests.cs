namespace SoulRipper.Tests;

public class TimelineTests
{
    private static readonly (ushort, byte)[] Idle = { (0x4017, 0xFF), (0x4015, 0x0F) };

    private static Timeline Of(params (ushort, byte)[][] frames)
    {
        var timeline = new Timeline();
        foreach (var frame in frames)
            timeline.Frames.Add(frame.ToList());
        return timeline;
    }

    [Fact]
    public void Writes_the_reference_also_makes_are_not_new()
    {
        var reference = Of(Idle, Idle);
        var capture = Of(Idle, Idle.Append(((ushort)0x4000, (byte)0x9F)).ToArray());

        Assert.Equal(new (ushort, byte)[] { (0x4000, 0x9F) }, capture.DiffFrame(1, reference));
    }

    // SMB2's title music ends after 28 s, past the 15 s reference: the idle
    // driver's writes after it must not keep the capture going.
    [Fact]
    public void Past_the_reference_the_idle_driver_writes_are_not_new()
    {
        var reference = Of(Idle, Idle);
        var capture = Of(Idle, Idle, Idle, Idle);

        Assert.Empty(capture.DiffFrame(3, reference));
    }
}
