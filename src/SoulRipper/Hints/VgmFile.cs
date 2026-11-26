using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SoulRipper.Hints;

// A track of a VGM pack: its GD3 title and the sound-chip writes it logs.
internal sealed record VgmTrack(string Title, IReadOnlyList<(ushort Address, byte Value)> Writes);

// Reads the NES APU (0xB4) and Game Boy DMG (0xB3) writes of a VGM or VGZ;
// other chips' commands are skipped by length. Writes to a second chip of
// the same kind (register bit 7) are left out.
internal static class VgmFile
{
    public static VgmTrack Read(byte[] data, string fallbackTitle)
    {
        if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gzip.CopyTo(plain);
            data = plain.ToArray();
        }
        if (data.Length < 0x40 || !data.AsSpan(0, 4).SequenceEqual("Vgm "u8))
            throw new InvalidDataException("not a VGM file");

        int Int(int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        var start = 0x40;
        if (Int(0x08) >= 0x150 && Int(0x34) != 0)
            start = 0x34 + Int(0x34);

        var writes = new List<(ushort, byte)>();
        var i = start;
        while (i < data.Length)
        {
            var command = data[i];
            if (command == 0x66)
                break;
            if (command == 0x67)
            {
                i += 7 + (i + 7 <= data.Length ? Int(i + 3) : 0);
                continue;
            }
            if (command is 0xB3 or 0xB4 && i + 2 < data.Length)
            {
                var register = data[i + 1];
                if ((register & 0x80) == 0)
                    writes.Add(((ushort)((command == 0xB3 ? 0xFF10 : 0x4000) + register), data[i + 2]));
            }
            i += Length(command);
        }

        return new VgmTrack(Title(data, Int(0x14)) is { Length: > 0 } title ? title : fallbackTitle, writes);
    }

    // Bytes taken by a command and its operands (VGM 1.71 command table).
    private static int Length(byte command) => command switch
    {
        0x61 => 3,
        >= 0x30 and <= 0x3F or 0x4F or 0x50 or 0x94 => 2,
        >= 0x40 and <= 0x4E or >= 0x51 and <= 0x5F or >= 0xA0 and <= 0xBF => 3,
        >= 0xC0 and <= 0xDF => 4,
        >= 0xE0 => 5,
        0x90 or 0x91 or 0x95 => 5,
        0x92 => 6,
        0x93 => 11,
        _ => 1,
    };

    // First GD3 field: the track's English title.
    private static string Title(byte[] data, int relative)
    {
        var at = 0x14 + relative + 12;
        if (relative == 0 || at >= data.Length)
            return "";
        var text = Encoding.Unicode.GetString(data, at, (data.Length - at) & ~1);
        var end = text.IndexOf('\0');
        return (end < 0 ? text : text[..end]).Trim();
    }
}
