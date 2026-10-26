using SoulRipper.Machines;

namespace SoulRipper;

// Plays the tracks of a GBS rip (the game's own driver code, plus INIT/PLAY
// entry points) on CoreBoy by wrapping it in an MBC5 ROM, and records what
// each track writes: sound registers per frame, and RAM during INIT (where it
// usually stores the game's request byte).
internal static class GbsPlayer
{
    private const int StubAddress = 0x0150;

    public static PlayedRip Play(byte[] gbs, int frames = 600)
    {
        if (gbs.Length < 0x70 || gbs[0] != 'G' || gbs[1] != 'B' || gbs[2] != 'S')
            throw new ArgumentException("not a GBS file");
        var count = gbs[4];
        var load = gbs[6] | gbs[7] << 8;
        if (load < 0x0200)
            throw new ArgumentException($"GBS load address {load:X4} leaves no room for the player stub");

        var tracks = new List<PlayedTrack>();
        var romPath = Path.Combine(Path.GetTempPath(), $"soul-ripper-gbs-{Environment.ProcessId}.gb");
        try
        {
            for (var track = 0; track < count; track++)
            {
                File.WriteAllBytes(romPath, WrapperRom(gbs, track));
                tracks.Add(RecordTrack(romPath, track, frames));
            }
        }
        finally
        {
            File.Delete(romPath);
        }
        return new PlayedRip("gb", tracks);
    }

    private static PlayedTrack RecordTrack(string romPath, int track, int frames)
    {
        using var m = new GameBoyMachine(romPath);
        var ram = new List<(ushort, byte)>();
        var initDone = false;
        m.Write = (a, v, _) =>
        {
            if (a == 0x6000) initDone = true;
            else if (!initDone && m.IsWorkRam(a)) ram.Add((a, v));
        };
        var t = Recorder.Record(m, frames);

        var writes = new List<(int, ushort, byte)>();
        for (var f = 0; f < t.Count; f++)
            foreach (var (a, v) in t.Frames[f])
                writes.Add((f, a, v));
        return new PlayedTrack(track, ram, writes);
    }

    private static byte[] WrapperRom(byte[] gbs, int track)
    {
        int Word(int o) => gbs[o] | gbs[o + 1] << 8;
        var load = Word(0x06);
        var init = Word(0x08);
        var play = Word(0x0A);
        var sp = Word(0x0C);
        var tma = gbs[0x0E];
        var tac = gbs[0x0F];
        var data = gbs.AsSpan(0x70);

        var size = 0x8000;
        while (size < load + data.Length) size <<= 1;
        var rom = new byte[size];
        data.CopyTo(rom.AsSpan(load));

        // RST vectors are relative to the load address in GBS rips.
        for (var rst = 0; rst < 0x40; rst += 8)
            Emit(rom, rst, 0xC3, (byte)(load + rst), (byte)(load + rst >> 8));
        Emit(rom, 0x40, 0xD9);                          // VBlank: reti
        Emit(rom, 0x50, 0xD9);                          // timer: reti

        Emit(rom, 0x100, 0x00, 0xC3, StubAddress & 0xFF, StubAddress >> 8);
        rom[0x147] = 0x1B;                              // MBC5 + RAM + battery
        rom[0x148] = (byte)(System.Numerics.BitOperations.Log2((uint)size) - 15);
        rom[0x149] = 0x03;                              // 32 KB RAM

        var useTimer = (tac & 0x04) != 0;
        Emit(rom, StubAddress,
            0xF3,                                       // di
            0x31, (byte)sp, (byte)(sp >> 8),            // ld sp, SP
            0x3E, 0x0A, 0xEA, 0x00, 0x00,               // enable cartridge RAM
            0x3E, 0x01, 0xEA, 0x00, 0x20,               // ROM bank 1
            0x3E, 0x80, 0xE0, 0x26,                     // NR52: sound on
            0x3E, 0x77, 0xE0, 0x24,                     // NR50: full volume
            0x3E, 0xFF, 0xE0, 0x25,                     // NR51: all channels
            0x3E, tma, 0xE0, 0x06,                      // TMA
            0x3E, tac, 0xE0, 0x07,                      // TAC
            0x3E, (byte)(useTimer ? 0x04 : 0x01), 0xE0, 0xFF,   // IE
            0x3E, (byte)track,                          // ld a, track
            0xCD, (byte)init, (byte)(init >> 8),        // call INIT
            0xEA, 0x00, 0x60,                           // ld ($6000), a: INIT done (no MBC5 register)
            0xFB,                                       // ei
            0x76, 0x00,                                 // loop: halt
            0xCD, (byte)play, (byte)(play >> 8),        // call PLAY
            0x18, 0xF9);                                // jr loop
        return rom;
    }

    private static void Emit(byte[] rom, int at, params int[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            rom[at + i] = (byte)bytes[i];
    }
}
