using SoulRipper.Machines;

namespace SoulRipper.Tests;

// A silent NES-like machine with plain RAM and optional scripted per-frame
// behaviour, for testing the prober and discovery without an emulator.
internal sealed class FakeMachine((ushort Address, byte Value)? crashAt = null) : IMachine
{
    private readonly byte[] ram = new byte[0x10000];
    private int frame;

    public SoundChip Chip => SoundChip.Nes;
    public Action<ushort, byte, ushort>? Write { get; set; }
    public Action<ushort, ushort>? Read { get; set; }
    public ushort StackPointer => 0x01FF;
    public Action? FrameEnded { get; set; }
    public byte[] ApuShadow { get; } = new byte[SoundChip.Nes.Length];
    public bool Audible { get; set; }
    public Action<FakeMachine, int>? OnFrame { get; set; }
    public (ushort CallSite, ushort Target)[] Stack { get; set; } = Array.Empty<(ushort, ushort)>();

    public IEnumerable<(ushort CallSite, ushort Target)> CallStack() => Stack;

    public byte BusRead(ushort address, ushort pc)
    {
        Read?.Invoke(address, pc);
        return ram[address];
    }

    public void BusWrite(ushort address, byte value, ushort pc)
    {
        if (Chip.Owns(address)) ApuShadow[address - Chip.BaseAddress] = value;
        else ram[address] = value;
        Write?.Invoke(address, value, pc);
    }

    public void RunFrames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (crashAt is var (address, value) && ram[address] == value)
                throw new InvalidOperationException("crashed");
            OnFrame?.Invoke(this, frame++);
            FrameEnded?.Invoke();
        }
    }

    public void Press(IReadOnlyList<string> buttons, int frames) => RunFrames(frames);
    public object SaveState() => ((byte[])ram.Clone(), frame, Audible);
    public void LoadState(object state)
    {
        var (saved, savedFrame, audible) = ((byte[], int, bool))state;
        saved.CopyTo(ram, 0);
        frame = savedFrame;
        Audible = audible;
    }
    public byte Peek(ushort address) => ram[address];
    public void Poke(ushort address, byte value) => ram[address] = value;
    public bool IsWorkRam(ushort address) => address < 0x0800;

    public void Dispose()
    {
    }
}
