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

    // Whether the audio output moved during the last frame: false once every
    // channel is silent, whatever the registers say.
    bool Audible { get; }

    void RunFrames(int count);
    void Press(IReadOnlyList<string> buttons, int frames);
    object SaveState();
    void LoadState(object state);
    byte Peek(ushort address);
    void Poke(ushort address, byte value);
    bool IsWorkRam(ushort address);

    // Active subroutine calls on the CPU stack, innermost first: each entry is
    // the call instruction's address and its jump target. Used to find a
    // banked sound driver's fixed-bank dispatcher.
    IEnumerable<(ushort CallSite, ushort Target)> CallStack() => Array.Empty<(ushort, ushort)>();

    // Subroutines in the ROM called with an immediate argument in A (e.g.
    // LDA #imm; JSR addr on NES, LD A,imm; CALL addr on GB), ranked by how
    // many distinct values they receive. Fallback when a game requests sounds
    // by calling routines directly rather than poking a RAM queue.
    IReadOnlyList<ushort> RoutineCandidates() => Array.Empty<ushort>();

    // Invokes the subroutine at `address` with `arg` in A until it returns,
    // restoring CPU registers afterwards. Returns false if it does not return
    // cleanly within `maxInstructions`.
    bool Call(ushort address, byte arg, int maxInstructions = 4000) => false;
}
