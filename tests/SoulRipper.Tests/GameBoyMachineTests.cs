using SoulRipper.Machines;

namespace SoulRipper.Tests;

public sealed class GameBoyMachineTests : IDisposable
{
    // CoreBoy only loads cartridges from a file.
    private readonly string rom = Path.Combine(Path.GetTempPath(), $"soulripper-test-{Guid.NewGuid():N}.gb");

    public GameBoyMachineTests() => File.WriteAllBytes(rom, IdleRom());

    public void Dispose() => File.Delete(rom);

    [Fact]
    public void Loading_a_state_forgets_the_sound_of_a_frame_cut_short()
    {
        using var gb = new GameBoyMachine(rom);
        gb.RunFrames(2);
        var silent = gb.SaveState();
        StartPulseNote(gb);
        // A request that crashes the game stops the frame half way, as an
        // exception thrown from inside the emulation does.
        var reads = 0;
        gb.Read = (_, _) =>
        {
            if (++reads == 5000) throw new InvalidOperationException("crashed");
        };
        Assert.Throws<InvalidOperationException>(() => gb.RunFrames(1));
        gb.Read = null;

        gb.LoadState(silent);
        gb.RunFrames(1);

        Assert.False(gb.Audible);
    }

    // Pulse 1 at full constant volume on both outputs with no length limit:
    // it plays until something stops it.
    private static void StartPulseNote(GameBoyMachine gb)
    {
        gb.Poke(0xFF26, 0x80);
        gb.Poke(0xFF24, 0x77);
        gb.Poke(0xFF25, 0x11);
        gb.Poke(0xFF11, 0x80);
        gb.Poke(0xFF12, 0xF0);
        gb.Poke(0xFF13, 0x00);
        gb.Poke(0xFF14, 0x87);
    }

    // 32 KB ROM-only cartridge whose code does nothing but loop, so only
    // pokes make sound.
    private static byte[] IdleRom()
    {
        var rom = new byte[0x8000];
        // $0100: NOP; JP $0150
        new byte[] { 0x00, 0xC3, 0x50, 0x01 }.CopyTo(rom, 0x100);
        // $0150: DI; loop: JR loop
        new byte[] { 0xF3, 0x18, 0xFE }.CopyTo(rom, 0x150);
        return rom;
    }
}
