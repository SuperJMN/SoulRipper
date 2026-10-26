namespace SoulRipper;

// Plays the tracks of an NSF rip (the game's own driver code, plus INIT/PLAY
// entry points) on a minimal 6502 with NSF bank switching, and records what
// each track writes: sound registers per frame, and RAM during INIT (where it
// usually stores the game's request byte).
internal static class NsfPlayer
{
    public static PlayedRip Play(byte[] nsf, int frames = 600)
    {
        if (nsf.Length < 0x80 || nsf[0] != 'N' || nsf[1] != 'E' || nsf[2] != 'S' || nsf[3] != 'M')
            throw new ArgumentException("not an NSF file");
        var tracks = Enumerable.Range(0, nsf[6]).Select(track => RecordTrack(nsf, track, frames)).ToList();
        return new PlayedRip("nes", tracks);
    }

    private static PlayedTrack RecordTrack(byte[] nsf, int track, int frames)
    {
        var bus = new NsfBus(nsf);
        var cpu = new Cpu6502(bus);
        var ram = new List<(ushort, byte)>();
        var writes = new List<(int, ushort, byte)>();
        var frame = -1;
        bus.Written = (a, v) =>
        {
            if (frame < 0 && (a < 0x0800 || a is >= 0x6000 and < 0x8000)) ram.Add(((ushort)a, (byte)v));
            if (frame >= 0 && (a is >= 0x4000 and <= 0x4013 || a is 0x4015 or 0x4017)) writes.Add((frame, (ushort)a, (byte)v));
        };

        for (var a = 0x4000; a <= 0x4013; a++) bus.Write((ushort)a, 0);
        bus.Write(0x4015, 0x0F);
        bus.Write(0x4017, 0x40);
        int Word(int o) => nsf[o] | nsf[o + 1] << 8;
        try
        {
            cpu.Call((ushort)Word(0x0A), (byte)track, 0);
            for (frame = 0; frame < frames; frame++)
                cpu.Call((ushort)Word(0x0C), 0, 0);
        }
        catch (InvalidOperationException)
        {
            // runaway or jammed code: keep what was recorded
        }
        return new PlayedTrack(track, ram, writes);
    }

    // NSF memory map: RAM, WRAM at $6000, 4 KB banks at $8000-$FFFF switched
    // through $5FF8-$5FFF (or a flat image when the rip is not bank switched).
    private sealed class NsfBus : IBus
    {
        private readonly byte[] ram = new byte[0x800];
        private readonly byte[] wram = new byte[0x2000];
        private readonly byte[] image;
        private readonly int[] banks = new int[8];

        public NsfBus(byte[] nsf)
        {
            var load = nsf[8] | nsf[9] << 8;
            var data = nsf.AsSpan(0x80);
            var banked = nsf.AsSpan(0x70, 8).IndexOfAnyExcept((byte)0) >= 0;
            if (banked)
            {
                var padding = load & 0x0FFF;
                image = new byte[(padding + data.Length + 0xFFF) & ~0xFFF];
                data.CopyTo(image.AsSpan(padding));
                for (var i = 0; i < 8; i++) banks[i] = nsf[0x70 + i];
            }
            else
            {
                image = new byte[0x8000];
                data[..Math.Min(data.Length, 0x10000 - load)].CopyTo(image.AsSpan(load - 0x8000));
                for (var i = 0; i < 8; i++) banks[i] = i;
            }
        }

        public Action<int, int>? Written { get; set; }

        public byte Read(ushort a) => a switch
        {
            < 0x2000 => ram[a & 0x7FF],
            >= 0x6000 and < 0x8000 => wram[a - 0x6000],
            >= 0x8000 => Rom(a),
            _ => 0,
        };

        private byte Rom(ushort a)
        {
            var offset = banks[(a - 0x8000) >> 12] * 0x1000 + (a & 0xFFF);
            return offset < image.Length ? image[offset] : (byte)0;
        }

        public void Write(ushort a, byte v)
        {
            switch (a)
            {
                case < 0x2000: ram[a & 0x7FF] = v; break;
                case >= 0x5FF8 and <= 0x5FFF: banks[a - 0x5FF8] = v; break;
                case >= 0x6000 and < 0x8000: wram[a - 0x6000] = v; break;
            }
            Written?.Invoke(a, v);
        }
    }
}
