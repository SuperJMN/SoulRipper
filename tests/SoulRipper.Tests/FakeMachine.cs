using SoulRipper.Machines;

namespace SoulRipper.Tests;

// A silent NES-like machine with plain RAM and no code. Its emulation crashes
// once `crashAt` holds its value, as some requests crash a real game.
internal sealed class FakeMachine((ushort Address, byte Value)? crashAt = null) : IMachine
{
    private readonly byte[] ram = new byte[0x10000];

    public SoundChip Chip => SoundChip.Nes;
    public Action<ushort, byte, ushort>? Write { get; set; }
    public Action<ushort, ushort>? Read { get; set; }
    public ushort StackPointer => 0x01FF;
    public Action? FrameEnded { get; set; }
    public byte[] ApuShadow { get; } = new byte[SoundChip.Nes.Length];
    public bool Audible => false;

    public void RunFrames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (crashAt is var (address, value) && ram[address] == value)
                throw new InvalidOperationException("crashed");
            FrameEnded?.Invoke();
        }
    }

    public void Press(IReadOnlyList<string> buttons, int frames) => RunFrames(frames);
    public object SaveState() => ram.Clone();
    public void LoadState(object state) => ((byte[])state).CopyTo(ram, 0);
    public byte Peek(ushort address) => ram[address];
    public void Poke(ushort address, byte value) => ram[address] = value;
    public bool IsWorkRam(ushort address) => address < 0x0800;

    public void Dispose()
    {
    }
}
