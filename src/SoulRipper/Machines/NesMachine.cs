using System.Reflection;
using System.Text.RegularExpressions;
using AprNes;

namespace SoulRipper.Machines;

// NES on the AprNes core (static, so one instance per process).
internal sealed class NesMachine : IMachine
{
    private static readonly Dictionary<string, byte> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 0, ["b"] = 1, ["select"] = 2, ["start"] = 3,
        ["up"] = 4, ["down"] = 5, ["left"] = 6, ["right"] = 7,
    };

    // The APU array sizes below are what initAPU (APU.cs) allocates in this
    // version of the core. A pointer carries no length, so a resized array
    // would be copied out of range without a word: check them before moving
    // to another version (the NES tests fail until then).
    private static readonly Version ApuCheckedOn = new(0, 0, 14, 0);

    // AprNes keeps the APU channels (timers, duty, volume, envelopes, sweeps,
    // length counters) in unmanaged arrays its savestate leaves out, so a loaded
    // state went on playing whatever the previous experiment left sounding and
    // a capture depended on what the same process ran before it (SMB2 gave a
    // 0.02 s 0603_80 in one rip of three). The machine saves them itself.
    private static readonly (FieldInfo Field, int Bytes)[] ApuArrays = ApuArraysOf(new (string, int)[]
    {
        ("_pulseTimer", 8), ("_pulsePeriod", 8), ("_pulseSeq", 8), ("_pulseDuty", 8), ("_pulseOut", 8),
        ("volume", 16), ("lengthctr", 16), ("lenCtrEnable", 4), ("apuRegister", 16),
        ("envelopeValue", 16), ("envelopeCounter", 16), ("envelopePos", 16), ("envConstVolume", 4), ("envelopeStartFlag", 4),
        ("sweepenable", 2), ("sweepnegate", 2), ("sweepsilence", 2), ("sweepreload", 2),
        ("sweepperiod", 8), ("sweepshift", 8), ("sweeppos", 8),
    });

    // CPU register and NMI fields on NesCore for direct subroutine invocation (Call).
    private static readonly FieldInfo RegA = CoreField("r_A");
    private static readonly FieldInfo RegX = CoreField("r_X");
    private static readonly FieldInfo RegY = CoreField("r_Y");
    private static readonly FieldInfo RegSp = CoreField("r_SP");
    private static readonly FieldInfo RegPc = CoreField("r_PC");
    private static readonly FieldInfo FlagN = CoreField("flagN");
    private static readonly FieldInfo FlagV = CoreField("flagV");
    private static readonly FieldInfo FlagD = CoreField("flagD");
    private static readonly FieldInfo FlagI = CoreField("flagI");
    private static readonly FieldInfo FlagZ = CoreField("flagZ");
    private static readonly FieldInfo FlagC = CoreField("flagC");
    private static readonly FieldInfo DoNmi = CoreField("doNMI");
    private static readonly FieldInfo DoIrq = CoreField("doIRQ");

    private static FieldInfo CoreField(string name) =>
        typeof(NesCore).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(NesCore), name);

    private static (FieldInfo, int)[] ApuArraysOf((string Name, int Bytes)[] arrays)
    {
        var core = typeof(NesCore).Assembly.GetName().Version;
        if (core != ApuCheckedOn)
            throw new NotSupportedException($"AprNes {core}: check the APU array sizes NesMachine saves (checked on {ApuCheckedOn})");
        return arrays.Select(x => (CoreField(x.Name), x.Bytes)).ToArray();
    }

    private readonly ushort[] routineCandidates;
    private readonly bool pal;
    private Action<ushort, ushort>? read;
    private int min = int.MaxValue, max = int.MinValue;

    public NesMachine(string romPath, bool pal = false) : this(File.ReadAllBytes(romPath), romPath, pal)
    {
    }

    public NesMachine(byte[] rom, string name = "ROM", bool pal = false)
    {
        this.pal = pal;
        if (!NesCore.DebugLoad(rom, pal ? NesCore.RegionType.PAL : NesCore.RegionType.NTSC))
            throw new InvalidOperationException($"AprNes rejected the ROM: {name}");
        routineCandidates = ScanRoutines(rom);
        NesCore.DebugSetMemoryObservers(OnWrite, null);
        NesCore.AudioSampleReady = (sample, _) =>
        {
            if (sample < min) min = sample;
            if (sample > max) max = sample;
        };
    }

    public SoundChip Chip => pal ? SoundChip.NesPal : SoundChip.Nes;

    // Detects whether an iNES / NES 2.0 ROM targets PAL (RP2A07, 50 Hz):
    // explicit NES 2.0 byte 12 wins; clean iNES 1.0 byte 9 bit 0 marks PAL,
    // while 0 falls back to regional tags in the No-Intro/GoodNES title or
    // filename because many European dumps leave byte 9 unset (Mr. Gimmick).
    public static bool DetectPal(byte[] rom, string? titleOrPath = null)
    {
        if (rom.Length >= 16 && rom[0] == 'N' && rom[1] == 'E' && rom[2] == 'S' && rom[3] == 0x1A)
        {
            var format = rom[7] & 0x0C;
            if (format == 0x08) // NES 2.0
            {
                var timing = rom[12] & 0x03;
                if (timing == 1) return true;
                if (timing == 0) return false;
            }
            else if (format == 0x00 && rom[12] == 0 && rom[13] == 0 && rom[14] == 0 && rom[15] == 0
                     && (rom[9] & 0x01) != 0)
            {
                return true;
            }
        }
        if (string.IsNullOrWhiteSpace(titleOrPath))
            return false;
        var tags = Regex.Matches(titleOrPath, @"\(([^)]+)\)").Select(m => m.Groups[1].Value).ToList();
        if (tags.Any(t => Regex.IsMatch(t, @"\b(USA|Japan|World|NTSC|U|J|JU|UE)\b", RegexOptions.IgnoreCase)))
            return false;
        return tags.Any(t => Regex.IsMatch(t,
            @"\b(Europe|PAL|Germany|France|Spain|Italy|Sweden|Netherlands|UK|Australia|Scandinavia|E|EU)\b",
            RegexOptions.IgnoreCase));
    }
    public Action<ushort, byte, ushort>? Write { get; set; }
    public ushort StackPointer => (ushort)(0x0100 | NesCore.DebugReadRegisters().Sp);

    // The output's DC filter drifts by a few units while silent; the quietest
    // pulse note swings by hundreds.
    public bool Audible { get; private set; }
    public Action? FrameEnded { get; set; }
    public byte[] ApuShadow { get; private set; } = new byte[SoundChip.Nes.Length];

    public Action<ushort, ushort>? Read
    {
        get => read;
        set
        {
            read = value;
            NesCore.DebugSetMemoryObservers(OnWrite, value);
        }
    }

    public void RunFrames(int count)
    {
        // Instruction stepping keeps the observers' PC accurate (DebugRunFrame
        // does not update it) at about the same speed.
        for (var i = 0; i < count; i++)
        {
            var start = NesCore.frame_count;
            while (NesCore.frame_count == start)
                NesCore.DebugStepInstruction();
            Audible = max - min > 64;
            (min, max) = (int.MaxValue, int.MinValue);
            FrameEnded?.Invoke();
        }
    }

    public void Press(IReadOnlyList<string> buttons, int frames)
    {
        foreach (var b in buttons) NesCore.P1_ButtonPress(Buttons[b]);
        RunFrames(frames);
        foreach (var b in buttons) NesCore.P1_ButtonUnPress(Buttons[b]);
    }

    public object SaveState()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            NesCore.DebugSaveState(writer);
        var apu = ApuArrays.SelectMany(a => Unmanaged(a.Field, a.Bytes).ToArray()).ToArray();
        return new State(stream.ToArray(), apu, (byte[])ApuShadow.Clone());
    }

    public void LoadState(object state)
    {
        var s = (State)state;
        using var reader = new BinaryReader(new MemoryStream(s.Core));
        NesCore.DebugLoadState(reader);
        var offset = 0;
        foreach (var (field, bytes) in ApuArrays)
        {
            s.Apu.AsSpan(offset, bytes).CopyTo(Unmanaged(field, bytes));
            offset += bytes;
        }
        ApuShadow = (byte[])s.Shadow.Clone();
        // Samples of a frame cut short (a crashed experiment) must not count
        // in the first frame from the loaded state.
        (min, max) = (int.MaxValue, int.MinValue);
    }

    private static unsafe Span<byte> Unmanaged(FieldInfo field, int bytes) =>
        new(Pointer.Unbox(field.GetValue(null)!), bytes);

    public byte Peek(ushort address)
    {
        var observer = read;
        Read = null;
        var value = NesCore.DebugReadCpu(address);
        if (observer != null) Read = observer;
        return value;
    }

    public void Poke(ushort address, byte value) => NesCore.DebugWriteCpu(address, value);

    // Internal RAM (minus the stack page) and cartridge work RAM.
    public bool IsWorkRam(ushort address) => address is < 0x0100 or >= 0x0200 and < 0x0800 or >= 0x6000 and < 0x8000;

    // 6502 JSR pushes (callSite + 2) leaving it little-endian on the stack;
    // only code ranges are peeked so PPU/APU registers ($2000-$5FFF) are never
    // side-effected.
    public IEnumerable<(ushort CallSite, ushort Target)> CallStack()
    {
        var calls = new List<(ushort, ushort)>();
        for (var s = StackPointer + 1; s < 0x01FF; s++)
        {
            var ret = (ushort)(Peek((ushort)s) | (Peek((ushort)(s + 1)) << 8));
            if (ret < 2) continue;
            var site = (ushort)(ret - 2);
            if (!IsCodeAddress(site) || Peek(site) != 0x20) continue;
            var target = (ushort)(Peek((ushort)(site + 1)) | (Peek((ushort)(site + 2)) << 8));
            calls.Add((site, target));
        }
        return calls;
    }

    private static bool IsCodeAddress(ushort a) => a is >= 0x8000 and <= 0xFFFD or < 0x07FE or >= 0x6000 and < 0x7FFE;

    public IReadOnlyList<ushort> RoutineCandidates() => routineCandidates;

    // Scans PRG ROM for `LDA #imm; JSR addr` (A9 nn 20 lo hi) targeting the
    // fixed bank (or anywhere in PRG on unbanked NROM). Games whose sound API
    // is a direct subroutine call (Kirby's Adventure $F826/$F859, Mega Man 2
    // $C051) show up near the top of the handful of matches.
    private static ushort[] ScanRoutines(byte[] rom)
    {
        if (rom.Length < 16 || rom[0] != 'N' || rom[1] != 'E' || rom[2] != 'S' || rom[3] != 0x1A)
            return Array.Empty<ushort>();
        var prgBanks = (int)rom[4];
        if ((rom[7] & 0x0C) == 0x08 && (rom[9] & 0x0F) != 0x0F)
            prgBanks |= (rom[9] & 0x0F) << 8;
        var prgOffset = 16 + ((rom[6] & 0x04) != 0 ? 512 : 0);
        var prgEnd = prgBanks > 0 ? Math.Min(rom.Length, prgOffset + prgBanks * 0x4000) : rom.Length;
        ushort minTarget = prgBanks is > 0 and <= 2 ? (ushort)0x8000 : (ushort)0xC000;
        var values = new Dictionary<ushort, HashSet<byte>>();
        for (var i = prgOffset; i + 4 < prgEnd; i++)
        {
            if (rom[i] != 0xA9 || rom[i + 2] != 0x20) continue;
            var target = (ushort)(rom[i + 3] | (rom[i + 4] << 8));
            if (target < minTarget) continue;
            if (!values.TryGetValue(target, out var set)) values[target] = set = new HashSet<byte>();
            set.Add(rom[i + 1]);
        }
        return values.Where(kv => kv.Value.Count >= 3)
            .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key)
            .Take(32).Select(kv => kv.Key).ToArray();
    }

    // Invokes a 6502 subroutine at `address` with `arg` in A (and matching Z/N
    // flags) by pushing a sentinel return address ($FFFE -> RTS lands at $FFFF)
    // and stepping until RTS returns to the original SP. Any NMI triggered
    // during the call (AprNes finishes frames at scanline 240, ~114 cycles
    // before scanline 241's NMI) is deferred until after Call returns so the
    // game's NMI handler never interrupts the subroutine or pollutes probing.
    public bool Call(ushort address, byte arg, int maxInstructions = 4000)
    {
        const ushort sentinel = 0xFFFF;
        var savedA = (byte)RegA.GetValue(null)!;
        var savedX = (byte)RegX.GetValue(null)!;
        var savedY = (byte)RegY.GetValue(null)!;
        var savedSp = (byte)RegSp.GetValue(null)!;
        var savedPc = (ushort)RegPc.GetValue(null)!;
        var savedN = (byte)FlagN.GetValue(null)!;
        var savedV = (byte)FlagV.GetValue(null)!;
        var savedD = (byte)FlagD.GetValue(null)!;
        var savedI = (byte)FlagI.GetValue(null)!;
        var savedZ = (byte)FlagZ.GetValue(null)!;
        var savedC = (byte)FlagC.GetValue(null)!;
        var deferredNmi = (bool)DoNmi.GetValue(null)!;
        var deferredIrq = (bool)DoIrq.GetValue(null)!;
        if (savedSp < 0x04)
            return false;

        // RTS pops lo, hi and increments PC by 1, so push (sentinel - 1).
        Poke((ushort)(0x0100 | savedSp), (byte)((sentinel - 1) >> 8));
        Poke((ushort)(0x0100 | (byte)(savedSp - 1)), (byte)((sentinel - 1) & 0xFF));
        RegSp.SetValue(null, (byte)(savedSp - 2));
        RegA.SetValue(null, arg);
        RegPc.SetValue(null, address);
        FlagI.SetValue(null, (byte)1);
        FlagD.SetValue(null, (byte)0);
        FlagZ.SetValue(null, (byte)(arg == 0 ? 1 : 0));
        FlagN.SetValue(null, (byte)((arg & 0x80) != 0 ? 1 : 0));
        DoNmi.SetValue(null, false);
        DoIrq.SetValue(null, false);

        var startFrame = NesCore.frame_count;
        var returned = false;
        try
        {
            for (var i = 0; i < maxInstructions; i++)
            {
                if ((bool)DoNmi.GetValue(null)!) { deferredNmi = true; DoNmi.SetValue(null, false); }
                if ((bool)DoIrq.GetValue(null)!) { deferredIrq = true; DoIrq.SetValue(null, false); }
                NesCore.DebugStepInstruction();
                if ((bool)DoNmi.GetValue(null)!) { deferredNmi = true; DoNmi.SetValue(null, false); }
                if ((bool)DoIrq.GetValue(null)!) { deferredIrq = true; DoIrq.SetValue(null, false); }
                if ((ushort)RegPc.GetValue(null)! == sentinel && (byte)RegSp.GetValue(null)! == savedSp)
                {
                    returned = true;
                    break;
                }
                if (NesCore.frame_count - startFrame > 1)
                    break;
            }
        }
        catch (Exception)
        {
            returned = false;
        }
        finally
        {
            RegA.SetValue(null, savedA);
            RegX.SetValue(null, savedX);
            RegY.SetValue(null, savedY);
            RegSp.SetValue(null, savedSp);
            RegPc.SetValue(null, savedPc);
            FlagN.SetValue(null, savedN);
            FlagV.SetValue(null, savedV);
            FlagD.SetValue(null, savedD);
            FlagI.SetValue(null, savedI);
            FlagZ.SetValue(null, savedZ);
            FlagC.SetValue(null, savedC);
            DoNmi.SetValue(null, deferredNmi);
            DoIrq.SetValue(null, deferredIrq);
        }
        return returned;
    }

    public void Dispose()
    {
        NesCore.DebugSetMemoryObservers(null, null);
        NesCore.AudioSampleReady = null;
        NesCore.exit = true;
    }

    private void OnWrite(ushort address, byte value, ushort pc)
    {
        if (Chip.Owns(address)) ApuShadow[address - Chip.BaseAddress] = value;
        Write?.Invoke(address, value, pc);
    }

    private sealed record State(byte[] Core, byte[] Apu, byte[] Shadow);
}
