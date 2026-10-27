using System.Reflection;
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

    // AprNes keeps the APU channels (timers, duty, volume, envelopes, sweeps,
    // length counters) in unmanaged arrays its savestate leaves out, so a loaded
    // state went on playing whatever the previous experiment left sounding and
    // a capture depended on what the same process ran before it (SMB2's 0604_20
    // lasted 0.90 s or 2.11 s). The machine saves them itself.
    private static readonly (FieldInfo Field, int Bytes)[] ApuArrays = new (string Name, int Bytes)[]
    {
        ("_pulseTimer", 8), ("_pulsePeriod", 8), ("_pulseSeq", 8), ("_pulseDuty", 8), ("_pulseOut", 8),
        ("volume", 16), ("lengthctr", 16), ("lenCtrEnable", 4), ("apuRegister", 16),
        ("envelopeValue", 16), ("envelopeCounter", 16), ("envelopePos", 16), ("envConstVolume", 4), ("envelopeStartFlag", 4),
        ("sweepenable", 2), ("sweepnegate", 2), ("sweepsilence", 2), ("sweepreload", 2),
        ("sweepperiod", 8), ("sweepshift", 8), ("sweeppos", 8),
    }.Select(x => (typeof(NesCore).GetField(x.Name, BindingFlags.Static | BindingFlags.NonPublic)
                   ?? throw new MissingFieldException(nameof(NesCore), x.Name), x.Bytes)).ToArray();

    private Action<ushort, ushort>? read;
    private int min = int.MaxValue, max = int.MinValue;

    public NesMachine(string romPath) : this(File.ReadAllBytes(romPath), romPath)
    {
    }

    public NesMachine(byte[] rom, string name = "ROM")
    {
        if (!NesCore.DebugLoad(rom))
            throw new InvalidOperationException($"AprNes rejected the ROM: {name}");
        NesCore.DebugSetMemoryObservers(OnWrite, null);
        NesCore.AudioSampleReady = (sample, _) =>
        {
            if (sample < min) min = sample;
            if (sample > max) max = sample;
        };
    }

    public SoundChip Chip => SoundChip.Nes;
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
