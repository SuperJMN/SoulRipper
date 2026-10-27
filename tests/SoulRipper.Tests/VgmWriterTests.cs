using System.Buffers.Binary;
using SoulRipper.Output;

namespace SoulRipper.Tests;

public class VgmWriterTests
{
    private static readonly Gd3 Tag = new("Track", "Game", "NES");
    private static readonly List<(int, ushort, byte)> Writes = new() { (0, 0x4015, 0x0F), (1, 0x4015, 0x1F) };
    private static readonly List<(int, ushort, byte[])> Kick = new() { (1, 0xE980, new byte[] { 0x55, 0xD5, 0xAA }) };

    // Data block 0x67 0x66, type 0xC2 (NES APU RAM), size, start address, bytes.
    private static readonly byte[] KickBlock = { 0x67, 0x66, 0xC2, 5, 0, 0, 0, 0x80, 0xE9, 0x55, 0xD5, 0xAA };

    [Fact]
    public void A_sample_is_loaded_before_the_writes_that_start_it()
    {
        var vgm = VgmWriter.Encode("nes", [], Writes, frames: 2, loopStart: null, Tag, samples: Kick);

        // Frame 0 ends with a 735-sample wait; frame 1 loads the sample, then enables the DMC.
        byte[] expected = [0x61, 0xDF, 0x02, .. KickBlock, 0xB4, 0x15, 0x1F];
        Assert.True(vgm.AsSpan().IndexOf(expected) > 0);
    }

    [Fact]
    public void A_sample_loaded_at_the_loop_start_is_loaded_on_every_pass()
    {
        var vgm = VgmWriter.Encode("nes", [], Writes, frames: 2, loopStart: 1, Tag, samples: Kick);

        var loop = 0x1C + BinaryPrimitives.ReadInt32LittleEndian(vgm.AsSpan(0x1C));
        Assert.Equal(KickBlock, vgm[loop..(loop + KickBlock.Length)]);
    }
}
