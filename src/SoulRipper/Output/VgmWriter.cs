using System.Buffers.Binary;
using System.Text;

namespace SoulRipper.Output;

// GD3 tag fields of a VGM.
internal sealed record Gd3(string Track, string Game, string System, string Author = "", string Date = "", string Notes = "");

// Encodes per-frame sound-chip register writes as VGM 1.61, for the NES APU
// (command 0xB4, clock at 0x84) and the Game Boy DMG (command 0xB3, clock at 0x80).
// NES DMC samples go in as APU RAM data blocks (type 0xC2) before their frame.
internal static class VgmWriter
{
    public const int SampleRate = 44100;
    private const int Header = 0x100;

    private sealed record Chip(int Clock, int ClockOffset, byte Command, ushort RegisterBase, double FramesPerSecond, (ushort, byte)[] Silence);

    private static readonly Chip Nes = new(1789772, 0x84, 0xB4, 0x4000, 60.0, new (ushort, byte)[] { (0x4015, 0x00) });
    private static readonly Chip GameBoy = new(4194304, 0x80, 0xB3, 0xFF10, 4194304.0 / 70224, new (ushort, byte)[] { (0xFF26, 0x00) });

    // `init` restores the chip state the capture started in. With
    // `loopStart`, frames loopStart..frames-1 loop forever; otherwise the chip
    // is silenced and a short tail appended. Returns the length in samples.
    public static byte[] Encode(string system, IEnumerable<(ushort Address, byte Value)> init,
        IReadOnlyList<(int Frame, ushort Address, byte Value)> writes, int frames, int? loopStart, Gd3 gd3, int tailFrames = 3,
        IReadOnlyList<(int Frame, ushort Address, byte[] Data)>? samples = null)
    {
        var chip = system == "gb" ? GameBoy : Nes;
        var byFrame = writes.ToLookup(w => w.Frame);
        var samplesByFrame = (samples ?? Array.Empty<(int, ushort, byte[])>()).ToLookup(b => b.Frame);
        var data = new List<byte>();
        var totalSamples = 0;
        var fraction = 0.0;
        var samplesPerFrame = SampleRate / chip.FramesPerSecond;

        void Register(ushort address, byte value) =>
            data.AddRange(new[] { chip.Command, (byte)(address - chip.RegisterBase), value });

        void DataBlock(ushort address, byte[] bytes)
        {
            data.AddRange(new byte[] { 0x67, 0x66, 0xC2 });
            data.AddRange(BitConverter.GetBytes(bytes.Length + 2));
            data.AddRange(new[] { (byte)address, (byte)(address >> 8) });
            data.AddRange(bytes);
        }

        void FrameWait()
        {
            fraction += samplesPerFrame;
            var n = (int)Math.Round(fraction, MidpointRounding.ToEven);
            fraction -= n;
            data.AddRange(new byte[] { 0x61, (byte)n, (byte)(n >> 8) });
            totalSamples += n;
        }

        foreach (var (a, v) in init)
            Register(a, v);

        int? loopOffset = null;
        var loopSamples = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            if (frame == loopStart)
                (loopOffset, loopSamples) = (data.Count, totalSamples);
            foreach (var (_, address, bytes) in samplesByFrame[frame])
                DataBlock(address, bytes);
            foreach (var w in byFrame[frame])
                Register(w.Address, w.Value);
            FrameWait();
        }

        if (loopOffset is null)
        {
            foreach (var (a, v) in chip.Silence)
                Register(a, v);
            for (var i = 0; i < tailFrames; i++)
                FrameWait();
        }
        data.Add(0x66);

        var tag = Gd3Block(gd3);
        var file = new byte[Header + data.Count + tag.Length];
        "Vgm "u8.CopyTo(file);
        void Put(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(offset), value);
        var gd3Position = Header + data.Count;
        Put(0x04, file.Length - 4);
        Put(0x08, 0x161);
        Put(0x14, gd3Position - 0x14);
        Put(0x18, totalSamples);
        if (loopOffset is { } lo)
        {
            Put(0x1C, Header + lo - 0x1C);
            Put(0x20, totalSamples - loopSamples);
        }
        Put(0x24, 60);
        Put(0x34, Header - 0x34);
        Put(chip.ClockOffset, chip.Clock);
        data.CopyTo(file, Header);
        tag.CopyTo(file, gd3Position);
        return file;
    }

    private static byte[] Gd3Block(Gd3 g)
    {
        var fields = new[] { g.Track, "", g.Game, "", g.System, "", g.Author, "", g.Date, "", g.Notes };
        var body = fields.SelectMany(f => Encoding.Unicode.GetBytes(f + "\0")).ToArray();
        var block = new byte[12 + body.Length];
        "Gd3 "u8.CopyTo(block);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), 0x100);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), body.Length);
        body.CopyTo(block, 12);
        return block;
    }
}
