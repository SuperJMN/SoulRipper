using CoreBoy;
using CoreBoy.controller;
using CoreBoy.gpu;
using CoreBoy.memory.cart;
using CoreBoy.serial;
using CoreBoy.sound;

namespace SoulRipper.Machines;

// Game Boy / Game Boy Color on the CoreBoy core.
internal sealed class GameBoyMachine : IMachine
{
    private const int CyclesPerFrame = 70224;

    private static readonly Dictionary<string, Button> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["right"] = Button.Right, ["left"] = Button.Left, ["up"] = Button.Up, ["down"] = Button.Down,
        ["a"] = Button.A, ["b"] = Button.B, ["select"] = Button.Select, ["start"] = Button.Start,
    };

    private readonly Gameboy gameboy;
    private readonly Controller controller = new();
    private readonly NullDisplay display = new();
    private readonly AudioMeter meter = new();
    private Action<ushort, ushort>? read;

    public GameBoyMachine(string romPath)
    {
        var options = new GameboyOptions { Rom = romPath, DisableBatterySaves = true, Headless = true };
        var cartridge = new Cartridge(options);
        gameboy = new Gameboy(options, cartridge, display, controller, meter, new NullSerialEndpoint());
        gameboy.Mmu.WriteObserver = OnWrite;
    }

    public SoundChip Chip => SoundChip.GameBoy;
    public Action<ushort, byte, ushort>? Write { get; set; }
    public byte[] ApuShadow { get; private set; } = new byte[SoundChip.GameBoy.Length];

    public ushort StackPointer => (ushort)gameboy.Cpu.Registers.SP;
    public bool Audible { get; private set; }
    public Action? FrameEnded { get; set; }

    public Action<ushort, ushort>? Read
    {
        get => read;
        set
        {
            read = value;
            gameboy.Mmu.ReadObserver = value == null ? null : OnRead;
        }
    }

    public void RunFrames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < CyclesPerFrame; c++)
                gameboy.Tick();
            Audible = meter.Swing() > 0;
            FrameEnded?.Invoke();
        }
    }

    public void Press(IReadOnlyList<string> buttons, int frames)
    {
        controller.Set(buttons.Select(b => Buttons[b]));
        RunFrames(frames);
        controller.Set(Array.Empty<Button>());
    }

    public object SaveState() =>
        new State(ObjectGraphSnapshot.Capture(gameboy, o => o is IDisplay or IController or ISoundOutput or SerialEndpoint),
            (byte[])ApuShadow.Clone());

    public void LoadState(object state)
    {
        var s = (State)state;
        s.Graph.Restore();
        ApuShadow = (byte[])s.Shadow.Clone();
        // Output of a frame cut short (a crashed experiment) must not count in
        // the first frame from the loaded state.
        meter.Reset();
    }

    public byte Peek(ushort address)
    {
        var observer = gameboy.Mmu.ReadObserver;
        gameboy.Mmu.ReadObserver = null;
        var value = (byte)gameboy.Mmu.GetByte(address);
        gameboy.Mmu.ReadObserver = observer;
        return value;
    }

    public void Poke(ushort address, byte value) => gameboy.Mmu.SetByte(address, value);

    // Cartridge RAM, work RAM and HRAM (games keep sound queues in all three).
    public bool IsWorkRam(ushort address) => address is >= 0xA000 and < 0xE000 or >= 0xFF80 and < 0xFFFF;

    public void Dispose()
    {
        gameboy.Mmu.WriteObserver = null;
        gameboy.Mmu.ReadObserver = null;
    }

    private void OnWrite(int address, int value)
    {
        var a = (ushort)address;
        if (Chip.Owns(a)) ApuShadow[a - Chip.BaseAddress] = (byte)value;
        Write?.Invoke(a, (byte)value, (ushort)gameboy.Cpu.InstructionAddress);
    }

    private void OnRead(int address) => read?.Invoke((ushort)address, (ushort)gameboy.Cpu.InstructionAddress);

    private sealed record State(ObjectGraphSnapshot Graph, byte[] Shadow);

    private sealed class Controller : IController
    {
        private readonly HashSet<Button> pressed = new();
        private IButtonListener? listener;

        public void SetButtonListener(IButtonListener listener) => this.listener = listener;

        public void Set(IEnumerable<Button> buttons)
        {
            var target = buttons.ToHashSet();
            foreach (var b in pressed.Where(b => !target.Contains(b)).ToList())
            {
                pressed.Remove(b);
                listener?.OnButtonRelease(b);
            }
            foreach (var b in target.Where(pressed.Add))
                listener?.OnButtonPress(b);
        }
    }

    private sealed class NullDisplay : IDisplay
    {
        public bool Enabled { get; set; }
        public event FrameProducedEventHandler? OnFrameProduced { add { } remove { } }
        public void PutDmgPixel(int color) { }
        public void PutColorPixel(int gbcRgb) { }
        public void RequestRefresh() { }
        public void WaitForRefresh() { }
        public void Run(CancellationToken token) { }
    }

    // Range of the mixed output since the last call.
    private sealed class AudioMeter : ISoundOutput
    {
        private int min = int.MaxValue, max = int.MinValue;

        public void Start() { }
        public void Stop() { }

        public void Play(int left, int right)
        {
            var v = left + right;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        public int Swing()
        {
            var swing = max >= min ? max - min : 0;
            Reset();
            return swing;
        }

        public void Reset() => (min, max) = (int.MaxValue, int.MinValue);
    }
}
