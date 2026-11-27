using System.Reflection;
using CoreBoy;
using CoreBoy.controller;
using CoreBoy.cpu;
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
    private readonly ushort[] routineCandidates;
    private readonly Controller controller = new();
    private readonly NullDisplay display = new();
    private readonly AudioMeter meter = new();
    private Action<ushort, ushort>? read;

    public GameBoyMachine(string romPath)
    {
        var options = new GameboyOptions { Rom = romPath, DisableBatterySaves = true, Headless = true };
        var cartridge = new Cartridge(options);
        gameboy = new Gameboy(options, cartridge, display, controller, meter, new NullSerialEndpoint());
        routineCandidates = ScanRoutines(File.ReadAllBytes(romPath));
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

    // SM83 CALL / CALL cc pushes (callSite + 3) in little-endian; only ROM and
    // RAM code ranges are peeked so hardware registers are never touched.
    public IEnumerable<(ushort CallSite, ushort Target)> CallStack()
    {
        var calls = new List<(ushort, ushort)>();
        var sp = StackPointer;
        var limit = Math.Min(0xFFFE, sp + 64);
        for (var s = sp; s < limit; s++)
        {
            var ret = (ushort)(Peek((ushort)s) | (Peek((ushort)(s + 1)) << 8));
            if (ret < 3) continue;
            var site = (ushort)(ret - 3);
            if (!IsCodeAddress(site)) continue;
            if (Peek(site) is not (0xCD or 0xC4 or 0xCC or 0xD4 or 0xDC)) continue;
            var target = (ushort)(Peek((ushort)(site + 1)) | (Peek((ushort)(site + 2)) << 8));
            calls.Add((site, target));
        }
        return calls;
    }

    private static bool IsCodeAddress(ushort a) => a is < 0x7FFD or >= 0xC000 and < 0xDFFD;

    public IReadOnlyList<ushort> RoutineCandidates() => routineCandidates;

    // Scans the ROM for `LD A, imm; CALL addr` (3E nn CD lo hi) targeting bank 0
    // (or anywhere in 32 KB unbanked ROMs).
    private static ushort[] ScanRoutines(byte[] rom)
    {
        ushort maxTarget = rom.Length <= 0x8000 ? (ushort)0x8000 : (ushort)0x4000;
        var values = new Dictionary<ushort, HashSet<byte>>();
        for (var i = 0; i + 4 < rom.Length; i++)
        {
            if (rom[i] != 0x3E || rom[i + 2] != 0xCD) continue;
            var target = (ushort)(rom[i + 3] | (rom[i + 4] << 8));
            if (target < 0x0100 || target >= maxTarget) continue;
            if (!values.TryGetValue(target, out var set)) values[target] = set = new HashSet<byte>();
            set.Add(rom[i + 1]);
        }
        return values.Where(kv => kv.Value.Count >= 3)
            .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key)
            .Take(32).Select(kv => kv.Key).ToArray();
    }

    private static readonly PropertyInfo CpuStateProp =
        typeof(Cpu).GetProperty(nameof(Cpu.State), BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMemberException(nameof(Cpu), nameof(Cpu.State));
    private static readonly FieldInfo CpuInterruptManagerField = CoreField(typeof(Cpu), "_interruptManager");
    private static readonly FieldInfo ImeField = CoreField(typeof(InterruptManager), "_ime");
    private static readonly FieldInfo PendingEnableField = CoreField(typeof(InterruptManager), "_pendingEnableInterrupts");
    private static readonly FieldInfo PendingDisableField = CoreField(typeof(InterruptManager), "_pendingDisableInterrupts");

    private static FieldInfo CoreField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(type.Name, name);

    internal bool InterruptMasterEnabled => gameboy.Cpu.InterruptMasterEnabled;

    // Invokes an SM83 subroutine at `address` with `arg` in A until RET lands
    // on the sentinel return address ($FFFF) at the original SP. Games often
    // end a frame in HALT waiting for VBlank, so HALT is suspended for the call
    // and restored afterwards, along with IME and all registers.
    public bool Call(ushort address, byte arg, int maxInstructions = 4000)
    {
        const int sentinel = 0xFFFF;
        // Finish the current instruction so CPU state is at an opcode or HALT boundary.
        for (var t = 0; t < 24 && gameboy.Cpu.State is not (CoreBoy.cpu.State.OPCODE or CoreBoy.cpu.State.HALTED); t++)
            gameboy.Tick();
        var savedState = gameboy.Cpu.State;
        if (savedState is not (CoreBoy.cpu.State.OPCODE or CoreBoy.cpu.State.HALTED))
            return false;

        var r = gameboy.Cpu.Registers;
        var savedA = r.A;
        var savedFlags = r.Flags.FlagsByte;
        var savedB = r.B;
        var savedC = r.C;
        var savedD = r.D;
        var savedE = r.E;
        var savedH = r.H;
        var savedL = r.L;
        var savedSp = r.SP;
        var savedPc = r.PC;
        var irq = (InterruptManager)CpuInterruptManagerField.GetValue(gameboy.Cpu)!;
        var savedIme = (bool)ImeField.GetValue(irq)!;
        var savedPendingEn = (int)PendingEnableField.GetValue(irq)!;
        var savedPendingDis = (int)PendingDisableField.GetValue(irq)!;

        if (savedState == CoreBoy.cpu.State.HALTED)
            CpuStateProp.SetValue(gameboy.Cpu, CoreBoy.cpu.State.OPCODE);
        irq.DisableInterrupts(false);

        var newSp = (savedSp - 2) & 0xFFFF;
        Poke((ushort)newSp, sentinel & 0xFF);
        Poke((ushort)((newSp + 1) & 0xFFFF), (sentinel >> 8) & 0xFF);
        r.SP = newSp;
        r.PC = address;
        r.A = arg;
        r.Flags.SetZ(arg == 0);

        var returned = false;
        try
        {
            var instructions = 0;
            for (var t = 0; t < maxInstructions * 24 && instructions < maxInstructions; t++)
            {
                var wasOpcode = gameboy.Cpu.State == CoreBoy.cpu.State.OPCODE;
                gameboy.Tick();
                if (!wasOpcode && gameboy.Cpu.State == CoreBoy.cpu.State.OPCODE)
                {
                    instructions++;
                    if (r.PC == sentinel && r.SP == savedSp)
                    {
                        returned = true;
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            returned = false;
        }
        finally
        {
            r.A = savedA;
            r.Flags.SetFlagsByte(savedFlags);
            r.B = savedB;
            r.C = savedC;
            r.D = savedD;
            r.E = savedE;
            r.H = savedH;
            r.L = savedL;
            r.SP = savedSp;
            r.PC = savedPc;
            CpuStateProp.SetValue(gameboy.Cpu, savedState);
            ImeField.SetValue(irq, savedIme);
            PendingEnableField.SetValue(irq, savedPendingEn);
            PendingDisableField.SetValue(irq, savedPendingDis);
        }
        return returned;
    }

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
