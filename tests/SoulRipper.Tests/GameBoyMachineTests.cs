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

    [Fact]
    public void CallStack_finds_an_active_call_return_address()
    {
        var callRom = Path.Combine(Path.GetTempPath(), $"soulripper-call-{Guid.NewGuid():N}.gb");
        File.WriteAllBytes(callRom, CallingDriverRom());
        try
        {
            using var gb = new GameBoyMachine(callRom);
            (ushort CallSite, ushort Target)[] observed = Array.Empty<(ushort, ushort)>();
            gb.Write = (a, _, _) =>
            {
                if (a == 0xFF26 && observed.Length == 0)
                    observed = gb.CallStack().ToArray();
            };

            gb.RunFrames(1);

            Assert.Contains(((ushort)0x0154, (ushort)0x0160), observed);
        }
        finally
        {
            File.Delete(callRom);
        }
    }

    [Fact]
    public void Call_works_when_halted_restores_state_and_rejects_runaway()
    {
        var routineRom = Path.Combine(Path.GetTempPath(), $"soulripper-routine-{Guid.NewGuid():N}.gb");
        File.WriteAllBytes(routineRom, RoutineRom());
        try
        {
            using var gb = new GameBoyMachine(routineRom);
            gb.RunFrames(1); // CPU is now in HALT with DI and known registers

            Assert.Contains((ushort)0x0170, gb.RoutineCandidates());
            Assert.True(gb.Call(0x0170, 0x42));
            Assert.Equal((byte)0x42, gb.Peek(0xC050));
            // Calling a runaway routine returns false and still leaves HALT/registers intact.
            Assert.False(gb.Call(0x0190, 0x01, maxInstructions: 50));
            // Calling $0170 a second time still works (SP and HALT state were preserved).
            Assert.True(gb.Call(0x0170, 0x99));
            Assert.Equal((byte)0x99, gb.Peek(0xC050));
            // Confirm registers and IME were restored (routine ran EI, but IME stays false).
            Assert.False(gb.InterruptMasterEnabled);
            Assert.True(gb.Call(0x01A0, 0x00));
            Assert.Equal((byte)0x22, gb.Peek(0xC060));
            Assert.Equal((byte)0x33, gb.Peek(0xC061));
            Assert.Equal((byte)0x44, gb.Peek(0xC062));
            Assert.Equal((byte)0x55, gb.Peek(0xC063));
            Assert.Equal((byte)0x66, gb.Peek(0xC064));
            Assert.Equal((byte)0x77, gb.Peek(0xC065));
        }
        finally
        {
            File.Delete(routineRom);
        }
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

    // 32 KB ROM-only cartridge that sets SP, calls $0160, and writes NR52 ($FF26).
    private static byte[] CallingDriverRom()
    {
        var rom = new byte[0x8000];
        // $0100: NOP; JP $0150
        new byte[] { 0x00, 0xC3, 0x50, 0x01 }.CopyTo(rom, 0x100);
        // $0150: DI; LD SP,$DFFE; CALL $0160; loop: JR loop
        new byte[] { 0xF3, 0x31, 0xFE, 0xDF, 0xCD, 0x60, 0x01, 0x18, 0xFE }.CopyTo(rom, 0x150);
        // $0160: LD A,$80; LDH ($26),A; RET
        new byte[] { 0x3E, 0x80, 0xE0, 0x26, 0xC9 }.CopyTo(rom, 0x160);
        return rom;
    }

    // 32 KB ROM-only cartridge that loads BC=$2233 DE=$4455 HL=$6677 and enters
    // HALT, with a routine at $0170 (clobbers BC/DE/HL, executes EI, stores A
    // to $C050, RET), a runaway loop at $0190, and a register-dump helper at $01A0.
    private static byte[] RoutineRom()
    {
        var rom = new byte[0x8000];
        // $0100: NOP; JP $0150
        new byte[] { 0x00, 0xC3, 0x50, 0x01 }.CopyTo(rom, 0x100);
        // $0150: DI; LD SP,$DFFE; LD BC,$2233; LD DE,$4455; LD HL,$6677; HALT; JR -2
        new byte[]
        {
            0xF3, 0x31, 0xFE, 0xDF,
            0x01, 0x33, 0x22,
            0x11, 0x55, 0x44,
            0x21, 0x77, 0x66,
            0x76, 0x18, 0xFE,
        }.CopyTo(rom, 0x150);
        // $0170: LD ($C050),A; LD BC,$0000; LD DE,$0000; LD HL,$0000; EI; RET
        new byte[] { 0xEA, 0x50, 0xC0, 0x01, 0x00, 0x00, 0x11, 0x00, 0x00, 0x21, 0x00, 0x00, 0xFB, 0xC9 }.CopyTo(rom, 0x170);
        // $0180: three call sites with distinct immediates
        new byte[]
        {
            0x3E, 0x01, 0xCD, 0x70, 0x01,
            0x3E, 0x02, 0xCD, 0x70, 0x01,
            0x3E, 0x03, 0xCD, 0x70, 0x01,
        }.CopyTo(rom, 0x180);
        // $0190: runaway: JR $0190
        new byte[] { 0x18, 0xFE }.CopyTo(rom, 0x190);
        // $01A0: dump B,C,D,E,H,L to $C060..$C065; RET
        new byte[]
        {
            0x78, 0xEA, 0x60, 0xC0, // LD A,B; LD ($C060),A
            0x79, 0xEA, 0x61, 0xC0, // LD A,C; LD ($C061),A
            0x7A, 0xEA, 0x62, 0xC0, // LD A,D; LD ($C062),A
            0x7B, 0xEA, 0x63, 0xC0, // LD A,E; LD ($C063),A
            0x7C, 0xEA, 0x64, 0xC0, // LD A,H; LD ($C064),A
            0x7D, 0xEA, 0x65, 0xC0, // LD A,L; LD ($C065),A
            0xC9,
        }.CopyTo(rom, 0x1A0);
        return rom;
    }
}
