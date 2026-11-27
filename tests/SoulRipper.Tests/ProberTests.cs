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

    [Fact]
    public void Discovery_falls_back_to_sound_routines_and_rejects_game_object_spawners()
    {
        // Models a game with no RAM request queue (Kirby's Adventure style):
        // $F859 starts music directly inside the driver (and $FF silences it),
        // $F826 starts SFX directly, and $CBA2 is a game object spawner that
        // writes dozens of WRAM bytes and happens to touch a bank variable.
        var machine = new FakeMachine
        {
            Routines = new ushort[] { 0xF859, 0xF826, 0xCBA2 },
        };
        machine.Poke(0x0600, 1); // title music playing initially

        machine.OnCall = (m, addr, arg) =>
        {
            if (addr == 0xF859)
            {
                if (arg == 0xFF) m.BusWrite(0x0600, 0, 0x8105);
                else if (arg is 0x00 or 0x01) m.BusWrite(0x0600, (byte)(arg + 1), 0x8105);
                return true;
            }
            if (addr == 0xF826)
            {
                if (arg is 0x00 or 0x01)
                {
                    m.BusWrite(0x0601, (byte)(arg + 1), 0x8028);
                    m.BusWrite(0x0602, 3, 0x8028);
                }
                return true;
            }
            if (addr == 0xCBA2)
            {
                // Touches bank variable $0048 plus >8 game-state bytes outside the driver.
                m.BusWrite(0x0048, 0x1D, 0xCBA5);
                for (ushort a = 0x0500; a < 0x0514; a++)
                    m.BusWrite(a, arg, 0xCBB6);
                return true;
            }
            return false;
        };

        machine.OnFrame = (m, f) =>
        {
            var song = m.BusRead(0x0600, 0x8158);
            var sfx = m.BusRead(0x0601, 0x8160);
            var timer = m.BusRead(0x0602, 0x8165);
            m.Audible = song != 0 || timer > 0;
            if (song != 0)
            {
                m.BusWrite(0x4002, (byte)((f & 0x07) + song * 16), 0x81D8);
                m.BusWrite(0x4006, (byte)((f & 0x03) + song * 8), 0x81F8);
            }
            if (timer > 0)
            {
                m.BusWrite(0x400E, (byte)(sfx * 4 + timer), 0x8235);
                m.BusWrite(0x0602, (byte)(timer - 1), 0x8169);
            }
            else if (song == 0)
            {
                m.BusWrite(0x4017, 0xC0, 0x8155);
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

        Assert.Equal("F859 = FF", discovery.Rebased);
        Assert.Equal(new[] { "F826_00", "F826_01", "F859_00", "F859_01" }, sounds.Select(s => s.Id).Order().ToArray());
        Assert.DoesNotContain(sounds, s => s.Address == 0xCBA2);
    }

    [Fact]
    public void Sweep_accepts_a_small_enqueue_routine_writing_driver_read_ram()
    {
        // Models Mega Man 2's $C051: writes only buffer + count (2 bytes),
        // without directly calling the banked driver during Call.
        var machine = new FakeMachine();
        machine.OnCall = (m, addr, arg) =>
        {
            if (addr != 0xC051) return false;
            var count = m.Peek(0x0066);
            m.BusWrite((ushort)(0x0580 + count), arg, 0xC057);
            m.BusWrite(0x0066, (byte)(count + 1), 0xC05A);
            return true;
        };
        machine.OnFrame = (m, _) =>
        {
            var count = m.BusRead(0x0066, 0xD0AD);
            if (count > 0)
            {
                var req = m.BusRead((ushort)(0x057F + count), 0xD0B1);
                m.BusWrite(0x0066, (byte)(count - 1), 0xD0BD);
                m.BusWrite(0x4002, req, 0x84EA);
            }
        };

        var settings = new DiscoverySettings { ProbeFrames = 4 };
        var prober = new Prober(machine, machine.SaveState(), settings);
        prober.Apply(new ProbeContext(
            new ushort[] { 0x84EA }, 0x01F0, 0x01FF, Array.Empty<ushort>(), null, new(), 0,
            CallMode: true, DriverReads: new ushort[] { 0x0066 }));

        var report = prober.Sweep(new ushort[] { 0xC051 })[0];

        Assert.True(report.IsTrigger);
        Assert.True(report.IsCall);
    }

    [Fact]
    public void Capture_in_call_mode_records_chip_writes_and_dmc_samples_made_during_call()
    {
        var machine = new FakeMachine();
        machine.Poke(0xC000, 0xAA);
        machine.OnCall = (m, addr, arg) =>
        {
            if (addr != 0xF826) return false;
            // Synchronous APU writes + DMC sample start inside Call itself.
            m.BusWrite(0x4003, arg, 0x8028);
            m.BusWrite(0x4012, 0x00, 0x8028);
            m.BusWrite(0x4013, 0x00, 0x8028);
            m.BusWrite(0x4015, 0x1F, 0x8028);
            m.Audible = true;
            return true;
        };
        machine.OnFrame = (m, f) =>
        {
            if (f >= 2) m.Audible = false;
        };

        var settings = new DiscoverySettings { ReferenceFrames = 10, MaxFrames = 30, QuietFrames = 4 };
        var prober = new Prober(machine, machine.SaveState(), settings);
        prober.Apply(new ProbeContext(
            new ushort[] { 0x8028 }, 0x01F0, 0x01FF, Array.Empty<ushort>(), null, new(), 0,
            CallMode: true));

        var sound = prober.Capture(0xF826, 0x07);

        Assert.True(sound.IsCall);
        Assert.Equal("sfx", sound.Kind);
        Assert.Contains((0, (ushort)0x4003, (byte)0x07), sound.Writes);
        Assert.Contains(sound.Samples!, s => s.Frame == 0 && s.Address == 0xC000 && s.Data[0] == 0xAA);
    }

    [Fact]
    public void Sweep_in_call_mode_rejects_non_driver_routines_that_write_hardware_registers()
    {
        // Models Journey to Silius $C6ED (MMC1 PRG bank switch): writes a
        // driver-read RAM variable ($00F1) plus mapper register $FFFF without
        // ever entering the sound driver.
        var machine = new FakeMachine();
        machine.OnCall = (m, addr, arg) =>
        {
            if (addr != 0xC6ED) return false;
            m.BusWrite(0x00F1, arg, 0xC6EE);
            m.BusWrite(0xFFFF, arg, 0xC6F6); // cartridge mapper register
            return true;
        };
        machine.OnFrame = (m, _) =>
        {
            var bank = m.BusRead(0x00F1, 0xC120);
            m.BusWrite(0x4002, bank, 0x8353);
        };

        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings { ProbeFrames = 4 });
        prober.Apply(new ProbeContext(
            new ushort[] { 0x8353 }, 0x01F0, 0x01FF, Array.Empty<ushort>(), null, new(), 0,
            CallMode: true, DriverReads: new ushort[] { 0x00F1 }));

        var report = prober.Sweep(new ushort[] { 0xC6ED })[0];

        Assert.Empty(report.Responses);
        Assert.False(report.IsTrigger);
    }

    [Fact]
    public void Sweep_in_call_mode_accepts_a_bank_switching_trampoline_that_enters_the_driver()
    {
        // A trampoline (Kirby $F859 / GB bank-0 trampoline) writes a mapper
        // register before calling into the driver's bank; that must be accepted
        // because execution enters the driver itself.
        var machine = new FakeMachine();
        machine.Poke(0x8105, 0x8D);
        machine.OnCall = (m, addr, arg) =>
        {
            if (addr != 0xF859) return false;
            m.BusWrite(0x8000, 0x1E, 0xF85B); // bank switch
            m.BusWrite(0x0600, arg, 0x8105);  // inside driver bank
            return true;
        };
        machine.OnFrame = (m, _) =>
        {
            var song = m.BusRead(0x0600, 0x8158);
            m.BusWrite(0x4002, song, 0x81D8);
        };

        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings { ProbeFrames = 4 });
        prober.Apply(new ProbeContext(
            new ushort[] { 0x81D8 }, 0x01F0, 0x01FF, Array.Empty<ushort>(), null, new(), 0,
            CallMode: true, DriverBanks: new[] { new DriverBank(0x8105, 0x0000008Du) }));

        var report = prober.Sweep(new ushort[] { 0xF859 })[0];

        Assert.True(report.IsTrigger);
        Assert.True(report.IsCall);
    }

    [Fact]
    public void Discovery_keeps_ram_state_when_early_routine_probe_finds_no_silencing_routine()
    {
        // When RAM has unsilenced triggers and routines fail to silence (or
        // find nothing), Discovery falls back to RAM capture and keeps RAM's
        // Addresses and Rebased state.
        var machine = new FakeMachine { Routines = new ushort[] { 0xE000 } };
        machine.Poke(0x0600, 1); // active music, no stop command anywhere
        machine.OnCall = (_, _, _) => false;
        machine.OnFrame = (m, f) =>
        {
            var sfx = m.BusRead(0x0040, 0x8200);
            if (sfx != 0)
            {
                m.BusWrite(0x0040, 0, 0x8204);
                if (sfx is 0x01 or 0x02) m.BusWrite(0x400E, sfx, 0x8210);
            }
            m.Audible = true;
            m.BusWrite(0x4002, (byte)(f & 0x07), 0x8220);
        };

        var settings = new DiscoverySettings
        {
            AnalysisFrames = 6,
            ProbeFrames = 4,
            ReferenceFrames = 20,
            MaxFrames = 30,
            QuietFrames = 4,
            MusicMinFrames = 20,
        };
        var prober = new Prober(machine, machine.SaveState(), settings);
        using var pool = new LocalPool(prober);
        var discovery = new Discovery(prober, pool, settings, _ => { });

        var sounds = discovery.Run();

        Assert.Null(discovery.Rebased);
        Assert.Contains(discovery.Addresses, a => a.Address == 0x0040 && a.IsTrigger);
        Assert.Equal(new[] { "0040_01", "0040_02" }, sounds.Select(s => s.Id).Order().ToArray());
    }

    [Fact]
    public void GameOwnedRam_freezes_writes_from_other_banks_sharing_the_drivers_address_window()
    {
        // Models Mr. Gimmick: bank 7 (sound driver) and bank $1B (intro logic)
        // both map into $8000-$9FFF. Writes from $8200 while another bank is
        // mapped must be frozen as game RAM, while driver writes stay unfrozen.
        var machine = new FakeMachine();
        void MapBank(byte b0, byte b1, byte b2, byte b3)
        {
            machine.Poke(0x849E, b0);
            machine.Poke(0x849F, b1);
            machine.Poke(0x84A0, b2);
            machine.Poke(0x84A1, b3);
        }

        MapBank(0x8D, 0x00, 0x40, 0xEA);
        machine.OnFrame = (m, f) =>
        {
            // Sound driver bank mapped at $8000-$9FFF.
            MapBank(0x8D, 0x00, 0x40, 0xEA);
            m.BusWrite(0x0600, (byte)(f & 0x0F), 0x8100);
            m.BusWrite(0x4002, 0x20, 0x849E);

            // Game intro bank mapped into the exact same $8000-$9FFF window.
            MapBank(0xA9, 0x01, 0x85, 0x42);
            m.BusWrite(0x0042, (byte)(f & 0xFF), 0x8200);
        };

        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings { AnalysisFrames = 4 });
        var analysis = prober.Analyze();
        prober.Apply(new ProbeContext(analysis.ApuPcs, analysis.MinSp, analysis.MaxSp,
            Array.Empty<ushort>(), null, new(), 0, DriverBanks: analysis.DriverBanks));

        var frozen = prober.GameOwnedRam();

        Assert.True(frozen.ContainsKey(0x0042));
        Assert.False(frozen.ContainsKey(0x0600));
    }

    [Fact]
    public void Discovery_caps_slow_silence_probes_and_tries_routines_before_unsilenced_ram_capture()
    {
        // Models Sunsoft drivers (Journey to Silius): multiple channel-state
        // false triggers produce >64 non-silencing distinct responses in RAM
        // while music plays, whereas routine $EA67 silences on $FF and plays
        // real music.
        var machine = new FakeMachine { Routines = new ushort[] { 0xEA67 } };
        machine.Poke(0x0600, 1); // title music playing

        machine.OnCall = (m, addr, arg) =>
        {
            if (addr != 0xEA67) return false;
            if (arg == 0xFF) m.BusWrite(0x0600, 0, 0x80A5);
            else if (arg is 0x01 or 0x02) m.BusWrite(0x0600, (byte)(arg + 1), 0x80A5);
            return true;
        };

        machine.OnFrame = (m, f) =>
        {
            var song = m.BusRead(0x0600, 0x816D);
            m.Audible = song != 0;
            if (song != 0)
            {
                // 4 false channel-state bytes ($00EB..$00EE): each yields 30
                // distinct chip perturbations (120 total > 64 cap) and resets
                // to 0, without ever silencing the active music.
                byte mix = 0;
                for (ushort a = 0x00EB; a <= 0x00EE; a++)
                {
                    var v = m.BusRead(a, 0x8189);
                    if (v is >= 1 and <= 30) mix ^= v;
                    if (v != 0) m.BusWrite(a, 0, 0x818D);
                }
                m.BusWrite(0x4002, (byte)((f & 0x07) + song * 16 + mix), 0x8353);
                m.BusWrite(0x4006, (byte)((f & 0x03) + song * 8), 0x8358);
            }
            else
            {
                m.BusWrite(0x4015, 0x00, 0x8353);
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
        using var inner = new LocalPool(prober);
        var spy = new SpyPool(inner);
        var discovery = new Discovery(prober, spy, settings, _ => { });

        var sounds = discovery.Run();

        Assert.Equal("EA67 = FF", discovery.Rebased);
        Assert.Equal(new[] { "EA67_01", "EA67_02" }, sounds.Select(s => s.Id).Order().ToArray());
        // Slow silence probing on the 120 unsilenced RAM responses was capped at 64.
        Assert.InRange(spy.RamSilentItems, 1, 80);
        // None of the unsilenced false RAM triggers were captured.
        Assert.DoesNotContain(spy.CapturedAddresses, a => a is >= 0x00EB and <= 0x00EE);
    }

    [Fact]
    public void Discovery_falls_back_to_routines_when_ram_trigger_produces_no_looping_music()
    {
        // Models Mr. Gimmick $0071: a RAM mode/pause flag silences on $FE and
        // replays non-looping jingle variants (0 looping music), hiding the
        // real sound routine $F2A3.
        var machine = new FakeMachine { Routines = new ushort[] { 0xF2A3 } };
        machine.Poke(0x0600, 1);    // active track (1..3)
        machine.Poke(0x0601, 0xFF); // looping music initially so base state is not silent

        machine.OnCall = (m, addr, arg) =>
        {
            if (addr != 0xF2A3) return false;
            if (arg == 0xFF)
            {
                m.BusWrite(0x0600, 0, 0x8077);
                m.BusWrite(0x0601, 0, 0x8077);
            }
            else if (arg is 0x01 or 0x02)
            {
                m.BusWrite(0x0600, (byte)(arg + 1), 0x80FC);
                m.BusWrite(0x0601, 0xFF, 0x80FC); // looping song
            }
            return true;
        };

        machine.OnFrame = (m, f) =>
        {
            var flag = m.BusRead(0x0071, 0x8246);
            if (flag == 0xFE)
            {
                m.BusWrite(0x0600, 0, 0x824A);
                m.BusWrite(0x0071, 0, 0x824E);
            }
            else if (flag is 0x01 or 0x03)
            {
                // Resumes a finite jingle (24 frames -> jingle, not looping music).
                m.BusWrite(0x0600, (byte)(flag == 0x01 ? 10 : 11), 0x824A);
                m.BusWrite(0x0601, 24, 0x824C);
                m.BusWrite(0x0071, 0, 0x824E);
            }

            var song = m.BusRead(0x0600, 0x8250);
            var rem = m.BusRead(0x0601, 0x8254);
            m.Audible = song != 0 && rem > 0;
            if (song != 0 && rem > 0)
            {
                m.BusWrite(0x4002, (byte)((f & 0x07) + song * 8), 0x849E);
                m.BusWrite(0x4006, (byte)((f & 0x03) + song * 4), 0x84BA);
                if (rem != 0xFF) m.BusWrite(0x0601, (byte)(rem - 1), 0x8258);
            }
            else
            {
                m.BusWrite(0x4015, 0x00, 0x849E);
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

        Assert.Equal("F2A3 = FF", discovery.Rebased);
        Assert.Equal(new[] { "F2A3_01", "F2A3_02" }, sounds.Select(s => s.Id).Order().ToArray());
        Assert.All(sounds, s => Assert.Equal("music", s.Kind));
    }

    private sealed class SpyPool(IWorkPool inner) : IWorkPool
    {
        public int RamSilentItems { get; private set; }
        public List<ushort> CapturedAddresses { get; } = new();

        public List<System.Text.Json.Nodes.JsonNode?> Map(string op, ProbeContext ctx,
            IReadOnlyList<System.Text.Json.Nodes.JsonNode?> items, Action<int, System.Text.Json.Nodes.JsonNode?>? done = null)
        {
            if (op == "silent" && !ctx.CallMode)
                RamSilentItems += items.Count;
            if (op == "capture")
                CapturedAddresses.AddRange(items.Where(i => i is not null).Select(i => (ushort)i![0]!));
            return inner.Map(op, ctx, items, done);
        }

        public void Dispose() => inner.Dispose();
    }
}
