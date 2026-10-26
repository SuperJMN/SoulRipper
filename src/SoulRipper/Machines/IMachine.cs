namespace SoulRipper.Machines;

// A console reduced to what sound discovery needs: run frames, snapshot and
// restore the whole machine, poke RAM and observe the CPU bus.
internal interface IMachine : IDisposable
{
    SoundChip Chip { get; }

    // Bus observers; both report the PC of the accessing instruction.
    Action<ushort, byte, ushort>? Write { get; set; }
    Action<ushort, ushort>? Read { get; set; }

    ushort StackPointer { get; }

    // Invoked after every emulated frame.
    Action? FrameEnded { get; set; }

    // Last value written to every sound-chip register since power on.
    byte[] ApuShadow { get; }

    void RunFrames(int count);
    void Press(IReadOnlyList<string> buttons, int frames);
    object SaveState();
    void LoadState(object state);
    byte Peek(ushort address);
    void Poke(ushort address, byte value);
    bool IsWorkRam(ushort address);
}
