namespace SoulRipper.Machines;

// Register layout of a sound chip: which addresses it owns, which channel each
// register drives, and which writes (re)start a note.
internal sealed class SoundChip
{
    public static readonly SoundChip Nes = new(
        name: "nes",
        baseAddress: 0x4000,
        length: 0x18,
        fps: 1789772.0 / 29780.5,
        channelOf: a => a switch
        {
            >= 0x4000 and <= 0x4003 => 0,   // pulse 1
            >= 0x4004 and <= 0x4007 => 1,   // pulse 2
            >= 0x4008 and <= 0x400B => 2,   // triangle
            >= 0x400C and <= 0x400F => 3,   // noise
            >= 0x4010 and <= 0x4013 => 4,   // DMC
            _ => -1,
        },
        // $4003/$4007/$400B/$400F reload the length counter and restart the note.
        isTrigger: a => a is 0x4003 or 0x4007 or 0x400B or 0x400F,
        // $4014 is OAM DMA and $4016 the controller strobe: not sound.
        isRegister: a => a is >= 0x4000 and <= 0x4013 or 0x4015 or 0x4017,
        // Channel enable first; the frame counter mode affects envelopes.
        initFirst: new ushort[] { 0x4015, 0x4017 });

    public static readonly SoundChip GameBoy = new(
        name: "gb",
        baseAddress: 0xFF10,
        length: 0x30,
        fps: 4194304.0 / 70224,
        channelOf: a => a switch
        {
            >= 0xFF10 and <= 0xFF14 => 0,   // square 1 (sweep)
            >= 0xFF16 and <= 0xFF19 => 1,   // square 2
            >= 0xFF1A and <= 0xFF1E => 2,   // wave
            >= 0xFF30 and <= 0xFF3F => 2,   // wave RAM
            >= 0xFF20 and <= 0xFF23 => 3,   // noise
            _ => -1,
        },
        // NRx4 with bit 7 set triggers; any NRx4 write is treated as one so the
        // init block never replays it.
        isTrigger: a => a is 0xFF14 or 0xFF19 or 0xFF1E or 0xFF23,
        isRegister: a => a is >= 0xFF10 and <= 0xFF26 or >= 0xFF30 and <= 0xFF3F,
        // Power must come first: NR52=0 clears every other register.
        initFirst: new ushort[] { 0xFF26, 0xFF24, 0xFF25 });

    private readonly Func<ushort, int> channelOf;
    private readonly Func<ushort, bool> isTrigger;
    private readonly Func<ushort, bool> isRegister;

    private SoundChip(string name, ushort baseAddress, int length, double fps,
        Func<ushort, int> channelOf, Func<ushort, bool> isTrigger, Func<ushort, bool> isRegister,
        ushort[] initFirst)
    {
        Name = name;
        BaseAddress = baseAddress;
        Length = length;
        FramesPerSecond = fps;
        this.channelOf = channelOf;
        this.isTrigger = isTrigger;
        this.isRegister = isRegister;
        InitFirst = initFirst;
    }

    public string Name { get; }
    public ushort BaseAddress { get; }
    public int Length { get; }
    public double FramesPerSecond { get; }
    public IReadOnlyList<ushort> InitFirst { get; }

    public bool Owns(ushort address) => address >= BaseAddress && address < BaseAddress + Length && isRegister(address);
    public int ChannelOf(ushort address) => channelOf(address);
    public bool IsTrigger(ushort address) => isTrigger(address);

    // Register writes that restore the chip configuration a capture starts in,
    // without restarting any note: control registers first, then the rest.
    public List<(ushort Address, byte Value)> InitWrites(byte[] shadow)
    {
        var writes = InitFirst.Select(a => (a, shadow[a - BaseAddress])).ToList();
        for (var i = 0; i < Length; i++)
        {
            var a = (ushort)(BaseAddress + i);
            if (Owns(a) && !IsTrigger(a) && !InitFirst.Contains(a))
                writes.Add((a, shadow[i]));
        }
        return writes;
    }
}
