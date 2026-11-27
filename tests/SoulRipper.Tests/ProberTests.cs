namespace SoulRipper.Tests;

public class ProberTests
{
    [Fact]
    public void A_crashed_capture_leaves_the_machine_observed_as_before()
    {
        var machine = new FakeMachine(crashAt: (0x0300, 0x01));
        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings());
        var observer = machine.Write;

        Assert.ThrowsAny<Exception>(() => prober.Capture(0x0300, 0x01));

        Assert.Same(observer, machine.Write);
    }

    [Fact]
    public void Analyze_includes_ram_read_near_a_banked_drivers_caller()
    {
        var machine = new FakeMachine { Stack = new[] { ((ushort)0xC12A, (ushort)0x8000) } };
        machine.OnFrame = (m, _) =>
        {
            // Fixed-bank dispatcher reads queue byte, then banked driver writes APU.
            m.BusRead(0x00C9, 0xC112);
            m.BusRead(0x0050, 0xE000); // unrelated game code far from caller
            m.BusWrite(0x4001, 0x08, 0x823F);
        };
        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings { AnalysisFrames = 4 });

        var analysis = prober.Analyze();

        Assert.Contains((ushort)0x00C9, analysis.Candidates);
        Assert.DoesNotContain((ushort)0x0050, analysis.Candidates);
        Assert.Equal(new ushort[] { 0x823F }, analysis.ApuPcs);
    }

    [Fact]
    public void Analyze_walks_nested_driver_calls_and_prioritizes_driver_ram_over_caller_ram()
    {
        var machine = new FakeMachine
        {
            // Inner call inside the driver ($8200 -> $84C0), outer call from fixed bank ($C12A -> $8000).
            Stack = new[] { ((ushort)0x8200, (ushort)0x84C0), ((ushort)0xC12A, (ushort)0x8000) },
        };
        machine.OnFrame = (m, _) =>
        {
            m.BusRead(0x00C9, 0xC112); // zero-page byte read near external caller
            m.BusRead(0x0370, 0x84CB); // high-address byte read inside the driver
            m.BusWrite(0x4002, 0x10, 0x84F8);
        };
        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings { AnalysisFrames = 4, MaxCandidates = 1 });

        var analysis = prober.Analyze();

        // Driver-read RAM ($0370) is kept ahead of lower-addressed caller RAM ($00C9).
        Assert.Equal(new ushort[] { 0x0370 }, analysis.Candidates);
    }

    [Fact]
    public void External_consumer_rule_only_allows_its_own_idle_clear()
    {
        var machine = new FakeMachine();
        machine.Poke(0x00C9, 0x88);
        machine.Poke(0x00CA, 0x88);
        machine.OnFrame = (m, f) =>
        {
            if (f == 0)
            {
                // Allowed: external consumer clears $00C9 back to idle $88.
                m.BusWrite(0x00C9, 0x88, 0xC11B);
                // Blocked: same PC writing a non-idle value to $00C9.
                m.BusWrite(0x00C9, 0x05, 0xC11B);
                // Blocked: same PC writing idle $88 to a different guarded address ($00CA).
                m.BusWrite(0x00CA, 0x88, 0xC11B);
            }
            m.BusWrite(0x4001, 0x08, 0x823F);
        };
        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings
        {
            ReferenceFrames = 2,
            MaxFrames = 2,
        });
        var ctx = new ProbeContext(
            new ushort[] { 0x823F }, 0x01F0, 0x01FF,
            new ushort[] { 0x00C9, 0x00CA },
            new (ushort, byte)[] { (0x00C9, 0x01), (0x00CA, 0x02) },
            new(), 0,
            new[] { new ConsumerRule(0x00C9, 0xC11B, 0x88) });

        prober.Apply(ctx);

        // $00C9 was cleared to $88 (then the non-idle write 0x05 was undone);
        // $00CA kept 0x02 because $C11B is only authorized to clear $00C9.
        Assert.Equal((byte)0x88, machine.Peek(0x00C9));
        Assert.Equal((byte)0x02, machine.Peek(0x00CA));
    }

    [Fact]
    public void Sweep_does_not_treat_changing_channel_state_as_a_trigger()
    {
        var machine = new FakeMachine();
        machine.Poke(0x037A, 0xBE);
        machine.OnFrame = (m, f) =>
        {
            var pitch = m.BusRead(0x037A, 0x84CB);
            m.BusWrite(0x4002, pitch, 0x84F8);
            if (f == 1) m.BusWrite(0x037A, 0x40, 0x8777); // next note overwrites pitch
        };
        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings { ProbeFrames = 4 });

        var report = prober.Sweep(new ushort[] { 0x037A })[0];

        Assert.False(report.IsTrigger);
    }

    [Fact]
    public void Discovery_rebases_a_ring_buffer_queue_and_drops_channel_state_false_triggers()
    {
        // Models a banked driver (APU writes at $823F/$84F8) dispatched from the
        // fixed bank ($C110-$C12A) via an 8-byte ring buffer at $00C8-$00CF
        // (empty sentinel $88, read index $00C7) plus a channel-pitch byte $037B
        // that is overwritten with 0 while music plays.
        const byte Empty = 0x88;
        var machine = new FakeMachine { Stack = new[] { ((ushort)0xC12A, (ushort)0x8000) } };
        machine.Poke(0x00C7, 1);
        for (ushort a = 0x00C8; a <= 0x00CF; a++) machine.Poke(a, Empty);
        machine.Poke(0x0300, 1); // active track: 1 = title music, 0 = silent, 2 = sfx

        machine.OnFrame = (m, f) =>
        {
            // Fixed-bank ring-buffer dispatcher at $C110-$C12A.
            var idx = m.BusRead(0x00C7, 0xC110);
            var slot = (ushort)(0x00C8 + (idx & 7));
            var req = m.BusRead(slot, 0xC112);
            if (req != Empty)
            {
                m.BusWrite(slot, Empty, 0xC11B);
                m.BusWrite(0x00C7, (byte)((idx + 1) & 7), 0xC121);
                if (req == 0xFF)
                {
                    m.BusWrite(0x0300, 0, 0x8192);
                    m.BusWrite(0x0301, 0, 0x8192);
                }
                else if (req is 0x01 or 0x02)
                {
                    m.BusWrite(0x0300, req, 0x8034);
                    m.BusWrite(0x0301, 4, 0x8034);
                }
            }

            // Driver update at $823F-$877B.
            var track = m.BusRead(0x0300, 0x824C);
            var timer = m.BusRead(0x0301, 0x8250);
            m.Audible = track != 0;
            if (track == 1)
            {
                // Playing music: $037B affects $4002 and is reset to 0 on frame 1.
                var detune = m.BusRead(0x037B, 0x84D6);
                m.BusWrite(0x4002, (byte)((f & 0x0F) + detune), 0x84F8);
                if (f % 3 == 1) m.BusWrite(0x037B, 0x00, 0x877B);
            }
            else if (track == 2 && timer > 0)
            {
                m.BusWrite(0x4003, (byte)(0x10 + timer), 0x84F8);
                timer--;
                m.BusWrite(0x0301, timer, 0x8255);
                if (timer == 0) m.BusWrite(0x0300, 0, 0x8255);
            }
            else
            {
                m.BusWrite(0x4001, 0x08, 0x823F);
            }
        };

        var settings = new DiscoverySettings
        {
            AnalysisFrames = 6,
            ProbeFrames = 4,
            ReferenceFrames = 30,
            MaxFrames = 60,
            QuietFrames = 6,
            MusicMinFrames = 20,
        };
        var prober = new Prober(machine, machine.SaveState(), settings);
        using var pool = new LocalPool(prober);
        var discovery = new Discovery(prober, pool, settings, _ => { });

        var sounds = discovery.Run();

        Assert.Equal("00C9 = FF", discovery.Rebased);
        Assert.Equal(new[] { "00CA_01", "00CA_02" }, sounds.Select(s => s.Id).Order().ToArray());
        Assert.Contains(discovery.Addresses, a => a.Address == 0x037B && !a.IsTrigger);
    }
}
