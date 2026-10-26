namespace SoulRipper;

internal interface IBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}

// Just enough 6502 to run sound drivers: official opcodes, no cycle timing,
// no interrupts. Call() runs a subroutine until it returns.
internal sealed class Cpu6502(IBus bus)
{
    private const ushort ReturnSentinel = 0xFFFF;
    private const int MaxInstructions = 2_000_000;

    private byte a, x, y, sp = 0xFD;
    private ushort pc;
    private bool c, z, i = true, d, v, n;

    public void Call(ushort address, byte accumulator, byte xIndex)
    {
        a = accumulator;
        x = xIndex;
        y = 0;
        // JSR pushes return-1; RTS adds 1 back.
        Push((byte)((ReturnSentinel - 1) >> 8));
        Push(unchecked((byte)(ReturnSentinel - 1)));
        pc = address;
        for (var count = 0; count < MaxInstructions; count++)
        {
            if (pc == ReturnSentinel)
                return;
            Step();
        }
        throw new InvalidOperationException($"subroutine at {address:X4} did not return");
    }

    private byte Fetch() => bus.Read(pc++);
    private ushort Fetch16() => (ushort)(Fetch() | Fetch() << 8);
    private ushort Read16(ushort at) => (ushort)(bus.Read(at) | bus.Read((ushort)(at + 1)) << 8);
    // Indirect JMP and zero-page pointers wrap within their page.
    private ushort Read16Wrapped(ushort at) =>
        (ushort)(bus.Read(at) | bus.Read((ushort)((at & 0xFF00) | (byte)(at + 1))) << 8);

    private void Push(byte value) => bus.Write((ushort)(0x100 | sp--), value);
    private byte Pull() => bus.Read((ushort)(0x100 | ++sp));

    private byte Flags(bool brk) => (byte)((n ? 0x80 : 0) | (v ? 0x40 : 0) | 0x20 | (brk ? 0x10 : 0)
                                           | (d ? 0x08 : 0) | (i ? 0x04 : 0) | (z ? 0x02 : 0) | (c ? 0x01 : 0));

    private void SetFlags(byte p)
    {
        n = (p & 0x80) != 0; v = (p & 0x40) != 0; d = (p & 0x08) != 0;
        i = (p & 0x04) != 0; z = (p & 0x02) != 0; c = (p & 0x01) != 0;
    }

    private byte Nz(byte value)
    {
        z = value == 0;
        n = (value & 0x80) != 0;
        return value;
    }

    // Effective address of the operand for each addressing mode.
    private ushort Zp() => Fetch();
    private ushort ZpX() => (byte)(Fetch() + x);
    private ushort ZpY() => (byte)(Fetch() + y);
    private ushort Abs() => Fetch16();
    private ushort AbsX() => (ushort)(Fetch16() + x);
    private ushort AbsY() => (ushort)(Fetch16() + y);
    private ushort IndX() => Read16Wrapped((byte)(Fetch() + x));
    private ushort IndY() => (ushort)(Read16Wrapped(Fetch()) + y);

    private void Adc(byte m)
    {
        var sum = a + m + (c ? 1 : 0);
        v = (~(a ^ m) & (a ^ sum) & 0x80) != 0;
        c = sum > 0xFF;
        a = Nz((byte)sum);
    }

    private void Cmp(byte reg, byte m)
    {
        c = reg >= m;
        Nz((byte)(reg - m));
    }

    private void Branch(bool taken)
    {
        var offset = (sbyte)Fetch();
        if (taken) pc = (ushort)(pc + offset);
    }

    private byte Asl(byte m) { c = (m & 0x80) != 0; return Nz((byte)(m << 1)); }
    private byte Lsr(byte m) { c = (m & 1) != 0; return Nz((byte)(m >> 1)); }
    private byte Rol(byte m) { var r = (byte)(m << 1 | (c ? 1 : 0)); c = (m & 0x80) != 0; return Nz(r); }
    private byte Ror(byte m) { var r = (byte)(m >> 1 | (c ? 0x80 : 0)); c = (m & 1) != 0; return Nz(r); }

    private void Modify(ushort at, Func<byte, byte> op) => bus.Write(at, op(bus.Read(at)));

    private void Step()
    {
        var op = Fetch();
        switch (op)
        {
            // loads and stores
            case 0xA9: a = Nz(Fetch()); break;
            case 0xA5: a = Nz(bus.Read(Zp())); break;
            case 0xB5: a = Nz(bus.Read(ZpX())); break;
            case 0xAD: a = Nz(bus.Read(Abs())); break;
            case 0xBD: a = Nz(bus.Read(AbsX())); break;
            case 0xB9: a = Nz(bus.Read(AbsY())); break;
            case 0xA1: a = Nz(bus.Read(IndX())); break;
            case 0xB1: a = Nz(bus.Read(IndY())); break;
            case 0xA2: x = Nz(Fetch()); break;
            case 0xA6: x = Nz(bus.Read(Zp())); break;
            case 0xB6: x = Nz(bus.Read(ZpY())); break;
            case 0xAE: x = Nz(bus.Read(Abs())); break;
            case 0xBE: x = Nz(bus.Read(AbsY())); break;
            case 0xA0: y = Nz(Fetch()); break;
            case 0xA4: y = Nz(bus.Read(Zp())); break;
            case 0xB4: y = Nz(bus.Read(ZpX())); break;
            case 0xAC: y = Nz(bus.Read(Abs())); break;
            case 0xBC: y = Nz(bus.Read(AbsX())); break;
            case 0x85: bus.Write(Zp(), a); break;
            case 0x95: bus.Write(ZpX(), a); break;
            case 0x8D: bus.Write(Abs(), a); break;
            case 0x9D: bus.Write(AbsX(), a); break;
            case 0x99: bus.Write(AbsY(), a); break;
            case 0x81: bus.Write(IndX(), a); break;
            case 0x91: bus.Write(IndY(), a); break;
            case 0x86: bus.Write(Zp(), x); break;
            case 0x96: bus.Write(ZpY(), x); break;
            case 0x8E: bus.Write(Abs(), x); break;
            case 0x84: bus.Write(Zp(), y); break;
            case 0x94: bus.Write(ZpX(), y); break;
            case 0x8C: bus.Write(Abs(), y); break;

            // transfers and stack
            case 0xAA: x = Nz(a); break;
            case 0xA8: y = Nz(a); break;
            case 0x8A: a = Nz(x); break;
            case 0x98: a = Nz(y); break;
            case 0xBA: x = Nz(sp); break;
            case 0x9A: sp = x; break;
            case 0x48: Push(a); break;
            case 0x68: a = Nz(Pull()); break;
            case 0x08: Push(Flags(true)); break;
            case 0x28: SetFlags(Pull()); break;

            // arithmetic and logic
            case 0x69: Adc(Fetch()); break;
            case 0x65: Adc(bus.Read(Zp())); break;
            case 0x75: Adc(bus.Read(ZpX())); break;
            case 0x6D: Adc(bus.Read(Abs())); break;
            case 0x7D: Adc(bus.Read(AbsX())); break;
            case 0x79: Adc(bus.Read(AbsY())); break;
            case 0x61: Adc(bus.Read(IndX())); break;
            case 0x71: Adc(bus.Read(IndY())); break;
            case 0xE9: Adc((byte)~Fetch()); break;
            case 0xE5: Adc((byte)~bus.Read(Zp())); break;
            case 0xF5: Adc((byte)~bus.Read(ZpX())); break;
            case 0xED: Adc((byte)~bus.Read(Abs())); break;
            case 0xFD: Adc((byte)~bus.Read(AbsX())); break;
            case 0xF9: Adc((byte)~bus.Read(AbsY())); break;
            case 0xE1: Adc((byte)~bus.Read(IndX())); break;
            case 0xF1: Adc((byte)~bus.Read(IndY())); break;
            case 0x29: a = Nz((byte)(a & Fetch())); break;
            case 0x25: a = Nz((byte)(a & bus.Read(Zp()))); break;
            case 0x35: a = Nz((byte)(a & bus.Read(ZpX()))); break;
            case 0x2D: a = Nz((byte)(a & bus.Read(Abs()))); break;
            case 0x3D: a = Nz((byte)(a & bus.Read(AbsX()))); break;
            case 0x39: a = Nz((byte)(a & bus.Read(AbsY()))); break;
            case 0x21: a = Nz((byte)(a & bus.Read(IndX()))); break;
            case 0x31: a = Nz((byte)(a & bus.Read(IndY()))); break;
            case 0x09: a = Nz((byte)(a | Fetch())); break;
            case 0x05: a = Nz((byte)(a | bus.Read(Zp()))); break;
            case 0x15: a = Nz((byte)(a | bus.Read(ZpX()))); break;
            case 0x0D: a = Nz((byte)(a | bus.Read(Abs()))); break;
            case 0x1D: a = Nz((byte)(a | bus.Read(AbsX()))); break;
            case 0x19: a = Nz((byte)(a | bus.Read(AbsY()))); break;
            case 0x01: a = Nz((byte)(a | bus.Read(IndX()))); break;
            case 0x11: a = Nz((byte)(a | bus.Read(IndY()))); break;
            case 0x49: a = Nz((byte)(a ^ Fetch())); break;
            case 0x45: a = Nz((byte)(a ^ bus.Read(Zp()))); break;
            case 0x55: a = Nz((byte)(a ^ bus.Read(ZpX()))); break;
            case 0x4D: a = Nz((byte)(a ^ bus.Read(Abs()))); break;
            case 0x5D: a = Nz((byte)(a ^ bus.Read(AbsX()))); break;
            case 0x59: a = Nz((byte)(a ^ bus.Read(AbsY()))); break;
            case 0x41: a = Nz((byte)(a ^ bus.Read(IndX()))); break;
            case 0x51: a = Nz((byte)(a ^ bus.Read(IndY()))); break;
            case 0xC9: Cmp(a, Fetch()); break;
            case 0xC5: Cmp(a, bus.Read(Zp())); break;
            case 0xD5: Cmp(a, bus.Read(ZpX())); break;
            case 0xCD: Cmp(a, bus.Read(Abs())); break;
            case 0xDD: Cmp(a, bus.Read(AbsX())); break;
            case 0xD9: Cmp(a, bus.Read(AbsY())); break;
            case 0xC1: Cmp(a, bus.Read(IndX())); break;
            case 0xD1: Cmp(a, bus.Read(IndY())); break;
            case 0xE0: Cmp(x, Fetch()); break;
            case 0xE4: Cmp(x, bus.Read(Zp())); break;
            case 0xEC: Cmp(x, bus.Read(Abs())); break;
            case 0xC0: Cmp(y, Fetch()); break;
            case 0xC4: Cmp(y, bus.Read(Zp())); break;
            case 0xCC: Cmp(y, bus.Read(Abs())); break;
            case 0x24: { var m = bus.Read(Zp()); z = (a & m) == 0; n = (m & 0x80) != 0; v = (m & 0x40) != 0; break; }
            case 0x2C: { var m = bus.Read(Abs()); z = (a & m) == 0; n = (m & 0x80) != 0; v = (m & 0x40) != 0; break; }

            // increments, decrements and shifts
            case 0xE6: Modify(Zp(), m => Nz((byte)(m + 1))); break;
            case 0xF6: Modify(ZpX(), m => Nz((byte)(m + 1))); break;
            case 0xEE: Modify(Abs(), m => Nz((byte)(m + 1))); break;
            case 0xFE: Modify(AbsX(), m => Nz((byte)(m + 1))); break;
            case 0xC6: Modify(Zp(), m => Nz((byte)(m - 1))); break;
            case 0xD6: Modify(ZpX(), m => Nz((byte)(m - 1))); break;
            case 0xCE: Modify(Abs(), m => Nz((byte)(m - 1))); break;
            case 0xDE: Modify(AbsX(), m => Nz((byte)(m - 1))); break;
            case 0xE8: x = Nz((byte)(x + 1)); break;
            case 0xC8: y = Nz((byte)(y + 1)); break;
            case 0xCA: x = Nz((byte)(x - 1)); break;
            case 0x88: y = Nz((byte)(y - 1)); break;
            case 0x0A: a = Asl(a); break;
            case 0x06: Modify(Zp(), Asl); break;
            case 0x16: Modify(ZpX(), Asl); break;
            case 0x0E: Modify(Abs(), Asl); break;
            case 0x1E: Modify(AbsX(), Asl); break;
            case 0x4A: a = Lsr(a); break;
            case 0x46: Modify(Zp(), Lsr); break;
            case 0x56: Modify(ZpX(), Lsr); break;
            case 0x4E: Modify(Abs(), Lsr); break;
            case 0x5E: Modify(AbsX(), Lsr); break;
            case 0x2A: a = Rol(a); break;
            case 0x26: Modify(Zp(), Rol); break;
            case 0x36: Modify(ZpX(), Rol); break;
            case 0x2E: Modify(Abs(), Rol); break;
            case 0x3E: Modify(AbsX(), Rol); break;
            case 0x6A: a = Ror(a); break;
            case 0x66: Modify(Zp(), Ror); break;
            case 0x76: Modify(ZpX(), Ror); break;
            case 0x6E: Modify(Abs(), Ror); break;
            case 0x7E: Modify(AbsX(), Ror); break;

            // control flow
            case 0x4C: pc = Abs(); break;
            case 0x6C: pc = Read16Wrapped(Abs()); break;
            case 0x20:
            {
                var target = Fetch16();
                var ret = (ushort)(pc - 1);
                Push((byte)(ret >> 8));
                Push((byte)ret);
                pc = target;
                break;
            }
            case 0x60: pc = (ushort)((Pull() | Pull() << 8) + 1); break;
            case 0x40: SetFlags(Pull()); pc = (ushort)(Pull() | Pull() << 8); break;
            case 0x00:
                Fetch();
                Push((byte)(pc >> 8));
                Push((byte)pc);
                Push(Flags(true));
                i = true;
                pc = Read16(0xFFFE);
                break;
            case 0x10: Branch(!n); break;
            case 0x30: Branch(n); break;
            case 0x50: Branch(!v); break;
            case 0x70: Branch(v); break;
            case 0x90: Branch(!c); break;
            case 0xB0: Branch(c); break;
            case 0xD0: Branch(!z); break;
            case 0xF0: Branch(z); break;

            // flags
            case 0x18: c = false; break;
            case 0x38: c = true; break;
            case 0x58: i = false; break;
            case 0x78: i = true; break;
            case 0xB8: v = false; break;
            case 0xD8: d = false; break;
            case 0xF8: d = true; break;
            case 0xEA: break;

            // unofficial NOPs some drivers contain
            case 0x1A or 0x3A or 0x5A or 0x7A or 0xDA or 0xFA: break;
            case 0x80 or 0x82 or 0x89 or 0xC2 or 0xE2 or 0x04 or 0x44 or 0x64 or 0x14 or 0x34 or 0x54 or 0x74 or 0xD4 or 0xF4: Fetch(); break;
            case 0x0C or 0x1C or 0x3C or 0x5C or 0x7C or 0xDC or 0xFC: Fetch16(); break;

            default:
                throw new InvalidOperationException($"unsupported opcode {op:X2} at {pc - 1:X4}");
        }
    }
}
