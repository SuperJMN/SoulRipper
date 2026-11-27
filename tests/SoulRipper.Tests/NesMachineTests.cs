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

    [Fact]
    public void CallStack_finds_an_active_jsr_return_address()
    {
        using var nes = new NesMachine(CallingDriverRom());
        (ushort CallSite, ushort Target)[] observed = Array.Empty<(ushort, ushort)>();
        nes.Write = (a, _, _) =>
        {
            if (a == 0x4001 && observed.Length == 0)
                observed = nes.CallStack().ToArray();
        };

        nes.RunFrames(1);

        Assert.Contains(((ushort)0xC002, (ushort)0xC010), observed);
    }

    [Fact]
    public void Call_invokes_subroutine_restores_registers_defers_nmi_and_rejects_runaway()
    {
        using var nes = new NesMachine(RoutineRom());
        nes.RunFrames(3); // lets PPU warm up and enable NMI
        var before = AprNes.NesCore.DebugReadRegisters();
        var nmiBefore = nes.Peek(0x0051);

        Assert.Contains((ushort)0xC020, nes.RoutineCandidates());
        Assert.True(nes.Call(0xC020, 0x37));
        Assert.Equal((byte)0x37, nes.Peek(0x0050));
        // NMI must not interrupt the subroutine even though it ran >114 cycles past SL 240.
        Assert.Equal(nmiBefore, nes.Peek(0x0051));
        var after = AprNes.NesCore.DebugReadRegisters();
        Assert.Equal((before.A, before.X, before.Y, before.Sp, before.Pc, before.Status),
            (after.A, after.X, after.Y, after.Sp, after.Pc, after.Status));

        // Runaway routine returns false and still restores every register.
        Assert.False(nes.Call(0xC040, 0x01, maxInstructions: 50));
        var afterFail = AprNes.NesCore.DebugReadRegisters();
        Assert.Equal((before.A, before.X, before.Y, before.Sp, before.Pc, before.Status),
            (afterFail.A, afterFail.X, afterFail.Y, afterFail.Sp, afterFail.Pc, afterFail.Status));

        // The deferred NMI still fires on the following frame.
        nes.RunFrames(1);
        Assert.True(nes.Peek(0x0051) > nmiBefore);
    }

    [Fact]
    public void DetectPal_checks_nes2_header_ines1_flag_and_regional_title_tags()
    {
        byte[] Header(byte b7, byte b9, byte b12) =>
            new byte[] { 0x4E, 0x45, 0x53, 0x1A, 1, 1, 0, b7, 0, b9, 0, 0, b12, 0, 0, 0 };

        // Explicit NES 2.0 timing byte wins over title tags; timing 2 (multi) falls back.
        Assert.True(NesMachine.DetectPal(Header(0x08, 0, 1), "Game (USA).nes"));
        Assert.False(NesMachine.DetectPal(Header(0x08, 0, 0), "Mr. Gimmick (Europe).nes"));
        Assert.True(NesMachine.DetectPal(Header(0x08, 0, 2), "Game (Europe).nes"));
        // Clean iNES 1.0 byte 9 bit 0 marks PAL; dirty trailing bytes ("DiskDude!") are ignored.
        Assert.True(NesMachine.DetectPal(Header(0x00, 1, 0), "game.nes"));
        var dirty = Header(0x00, 1, (byte)'!');
        Assert.False(NesMachine.DetectPal(dirty, "game.nes"));
        // Short / non-iNES buffers fall back to title tags safely.
        Assert.True(NesMachine.DetectPal(Array.Empty<byte>(), "Mr. Gimmick (E).nes"));
        Assert.False(NesMachine.DetectPal(Array.Empty<byte>(), "Mr. Gimmick (UE).nes"));
        // iNES 1.0 byte 9 == 0 falls back to European title tags (Mr. Gimmick).
        Assert.True(NesMachine.DetectPal(Header(0x00, 0, 0), "Mr. Gimmick (Europe).nes"));
        Assert.False(NesMachine.DetectPal(Header(0x00, 0, 0), "Journey to Silius (USA).nes"));
        Assert.False(NesMachine.DetectPal(Header(0x00, 0, 0), "Super Mario Bros. (World).nes"));
        Assert.False(NesMachine.DetectPal(Header(0x00, 0, 0), "Legend of Zelda (USA, Europe).nes"));
    }

    [Fact]
    public void Pal_machine_runs_with_rp2a07_timing_replays_savestates_and_switches_back_to_ntsc()
    {
        using (var pal = new NesMachine(IdleRom(), pal: true))
        {
            Assert.Same(SoundChip.NesPal, pal.Chip);
            Assert.True(pal.Chip.IsPal);
            Assert.Equal(AprNes.NesCore.RegionType.PAL, AprNes.NesCore.Region);

            pal.RunFrames(2);
            var state = pal.SaveState();
            StartPulseNote(pal);
            pal.RunFrames(1);
            Assert.True(pal.Audible);

            pal.LoadState(state);
            pal.RunFrames(1);
            Assert.False(pal.Audible);
        }

        using var ntsc = new NesMachine(IdleRom(), pal: false);
        Assert.Same(SoundChip.Nes, ntsc.Chip);
        Assert.False(ntsc.Chip.IsPal);
        Assert.Equal(AprNes.NesCore.RegionType.NTSC, AprNes.NesCore.Region);
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

    // NROM cartridge whose RESET calls a subroutine at $C010 that writes $4001.
    private static byte[] CallingDriverRom()
    {
        var header = new byte[] { 0x4E, 0x45, 0x53, 0x1A, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var prg = new byte[0x4000];
        // $C000: SEI; CLD; JSR $C010; loop: JMP loop
        new byte[] { 0x78, 0xD8, 0x20, 0x10, 0xC0, 0x4C, 0x05, 0xC0 }.CopyTo(prg, 0);
        // $C010: LDA #$08; STA $4001; RTS
        new byte[] { 0xA9, 0x08, 0x8D, 0x01, 0x40, 0x60 }.CopyTo(prg, 0x10);
        // NMI, RESET and IRQ vectors.
        new byte[] { 0x05, 0xC0, 0x00, 0xC0, 0x05, 0xC0 }.CopyTo(prg, 0x3FFA);
        return header.Concat(prg).Concat(new byte[0x2000]).ToArray();
    }

    // NROM cartridge with NMI enabled (increments $51), a subroutine at $C020
    // that clobbers X/Y, burns >300 cycles, stores A to $50 and returns, and a
    // runaway loop at $C040.
    private static byte[] RoutineRom()
    {
        var header = new byte[] { 0x4E, 0x45, 0x53, 0x1A, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var prg = new byte[0x4000];
        // $C000: SEI; CLD; LDA #$80; STA $2000; LDA #$11; LDX #$22; LDY #$33; loop: JMP loop ($C00D)
        new byte[] { 0x78, 0xD8, 0xA9, 0x80, 0x8D, 0x00, 0x20, 0xA9, 0x11, 0xA2, 0x22, 0xA0, 0x33, 0x4C, 0x0D, 0xC0 }
            .CopyTo(prg, 0);
        // $C020: STA $50; LDX #$40; LDY #$99; spin: DEX; BNE spin; RTS
        new byte[] { 0x85, 0x50, 0xA2, 0x40, 0xA0, 0x99, 0xCA, 0xD0, 0xFD, 0x60 }.CopyTo(prg, 0x20);
        // $C030: three call sites with distinct immediates
        new byte[]
        {
            0xA9, 0x01, 0x20, 0x20, 0xC0,
            0xA9, 0x02, 0x20, 0x20, 0xC0,
            0xA9, 0x03, 0x20, 0x20, 0xC0,
        }.CopyTo(prg, 0x30);
        // $C040: runaway: JMP $C040
        new byte[] { 0x4C, 0x40, 0xC0 }.CopyTo(prg, 0x40);
        // $C050: NMI handler: INC $51; RTI
        new byte[] { 0xE6, 0x51, 0x40 }.CopyTo(prg, 0x50);
        new byte[] { 0x50, 0xC0, 0x00, 0xC0, 0x0D, 0xC0 }.CopyTo(prg, 0x3FFA);
        return header.Concat(prg).Concat(new byte[0x2000]).ToArray();
    }
}
