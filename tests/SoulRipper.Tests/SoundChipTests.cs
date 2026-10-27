using SoulRipper.Machines;

namespace SoulRipper.Tests;

public class SoundChipTests
{
    private static byte[] NesRegisters(byte sampleAddress, byte sampleLength)
    {
        var shadow = new byte[SoundChip.Nes.Length];
        shadow[0x12] = sampleAddress;
        shadow[0x13] = sampleLength;
        return shadow;
    }

    [Fact]
    public void Enabling_the_DMC_plays_the_sample_its_registers_describe()
    {
        var sample = SoundChip.Nes.SampleStart(0x4015, 0x1F, NesRegisters(0xAA, 0x38));

        Assert.Equal(((ushort)0xEA80, 897), sample);
    }

    [Fact]
    public void Enabling_only_the_other_channels_plays_no_sample()
    {
        var sample = SoundChip.Nes.SampleStart(0x4015, 0x0F, NesRegisters(0xAA, 0x38));

        Assert.Null(sample);
    }
}
