namespace SoulRipper.Tests;

public sealed class TimelineTests
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

    // SML's idle driver writes twice in one frame of 14, and the reference
    // ends on a frame with one write: past it, the second writes must not
    // keep Can-Can going as a silent loop.
    [Fact]
    public void Past_the_reference_idle_writes_that_vary_between_frames_are_not_new()
    {
        var twice = Idle.Concat(Idle).ToArray();
        var reference = Of(twice, Idle, Idle, Idle, twice, Idle, Idle, Idle, twice, Idle, Idle, Idle);
        var capture = Of(Idle, Idle, Idle, Idle, Idle, Idle, Idle, Idle, Idle, Idle, Idle, Idle, twice);

        Assert.Empty(capture.DiffFrame(12, reference));
    }

    // A sound still playing past the reference keeps writing on top of the
    // idle driver; those writes must count, or every long sound ends there.
    [Fact]
    public void Past_the_reference_writes_the_idle_driver_does_not_make_are_new()
    {
        var reference = Of(Idle, Idle);
        var capture = Of(Idle, Idle, Idle, Idle.Append(((ushort)0x4000, (byte)0x9F)).ToArray());

        Assert.Equal(new (ushort, byte)[] { (0x4000, 0x9F) }, capture.DiffFrame(3, reference));
    }
}
