using SoulRipper.Machines;

namespace SoulRipper.Tests;

// The AprNes core is static, so this is the only test class that runs a NES.
public class NesMachineTests
{
    [Fact]
    public void Loading_a_state_silences_a_note_started_after_it_was_saved()
    {
        using var nes = new NesMachine(IdleRom());
        nes.RunFrames(2);
        var silent = nes.SaveState();
        StartPulseNote(nes);
        nes.RunFrames(1);
        var playing = nes.Audible;

        nes.LoadState(silent);
        nes.RunFrames(1);

        Assert.Equal((true, false), (playing, nes.Audible));
    }

    [Fact]
    public void Loading_a_state_forgets_the_sound_of_a_frame_cut_short()
    {
        using var nes = new NesMachine(IdleRom());
        nes.RunFrames(2);
        var silent = nes.SaveState();
        StartPulseNote(nes);
        // A request that crashes the game stops the frame half way, as an
        // exception thrown from inside the emulation does.
        var reads = 0;
        nes.Read = (_, _) =>
        {
            if (++reads == 5000) throw new InvalidOperationException("crashed");
        };
        Assert.Throws<InvalidOperationException>(() => nes.RunFrames(1));
        nes.Read = null;

        nes.LoadState(silent);
        nes.RunFrames(1);

        Assert.False(nes.Audible);
    }

    // Pulse 1 at full constant volume with its length counter halted: it
    // plays until something stops it.
    private static void StartPulseNote(NesMachine nes)
    {
        nes.Poke(0x4015, 0x01);
        nes.Poke(0x4000, 0xBF);
        nes.Poke(0x4002, 0xFD);
        nes.Poke(0x4003, 0x00);
    }

    // NROM cartridge whose code does nothing but loop, so only pokes make sound.
    private static byte[] IdleRom()
    {
        var header = new byte[] { 0x4E, 0x45, 0x53, 0x1A, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var prg = new byte[0x4000];
        // $C000: SEI; CLD; loop: JMP loop
        new byte[] { 0x78, 0xD8, 0x4C, 0x02, 0xC0 }.CopyTo(prg, 0);
        // NMI, RESET and IRQ vectors.
        new byte[] { 0x02, 0xC0, 0x00, 0xC0, 0x02, 0xC0 }.CopyTo(prg, 0x3FFA);
        return header.Concat(prg).Concat(new byte[0x2000]).ToArray();
    }
}
