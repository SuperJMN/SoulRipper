using System.Text.Json.Nodes;

namespace SoulRipper;

// Finds a game's sound request bytes by brute force and captures every sound
// they trigger, without any game-specific knowledge:
//  1. analysis: locate the sound driver and the RAM it reads;
//  2. sweep: poke candidate bytes and keep those the driver consumes;
//  3. silence: if music plays, find the request that stops it and start
//     over from the silence;
//  4. capture: record each request until its sound ends or loops.
// The heavy steps run on a pool of probers.
internal sealed class Discovery(Prober local, IWorkPool pool, DiscoverySettings s, Action<string> log)
{
    // How the base state was silenced, if it was.
    public string? Rebased { get; private set; }
    public int CandidateCount { get; private set; }
    public IReadOnlyList<ushort> ApuWriterPcs { get; private set; } = Array.Empty<ushort>();
    public IReadOnlyList<AddressReport> Addresses { get; private set; } = Array.Empty<AddressReport>();

    // `queues` skips the search and probes only those bytes; `extra` adds
    // bytes to the search (hints from elsewhere, e.g. a community rip).
    // `analysis` locates the driver from another state, for a base state
    // where it is idle; `idleState` is a state where the driver is idle, to
    // silence it.
    public List<Sound> Run(IReadOnlyList<ushort>? queues = null, IReadOnlyList<ushort>? extra = null,
        Analysis? analysis = null, object? idleState = null)
    {
        var (apuPcs, minSp, maxSp, candidates, driverBanks) = analysis ?? local.Analyze();
        ApuWriterPcs = apuPcs;
        if (apuPcs.Length == 0)
            log("warning: the driver wrote no sound registers during analysis; probing all RAM it touched");
        candidates = queues?.ToList() ?? candidates.Union(extra ?? Array.Empty<ushort>()).ToList();
        CandidateCount = candidates.Count;
        log($"driver: {apuPcs.Length} sound-writing PCs ({string.Join(" ", apuPcs.Take(8).Select(p => p.ToString("X4")))}), " +
            $"{candidates.Count} candidate bytes");

        var ctx = Applied(new ProbeContext(apuPcs, minSp, maxSp, Array.Empty<ushort>(), null, new(), 0,
            DriverBanks: driverBanks));
        var ramAddresses = Sweep(ctx, candidates);
        Addresses = ramAddresses;
        var triggers = ramAddresses.Where(a => a.IsTrigger).ToList();
        log($"triggers: {string.Join(", ", triggers.Select(t => $"{t.Address:X4} ({t.Responses.Count} values)"))}");

        var ramSilent = false;
        string? ramRebased = null;
        if (triggers.Count > 0)
        {
            ctx = Applied(ctx with { Guarded = triggers.Select(t => t.Address).ToArray(), Consumers = ConsumersOf(triggers) });
            if (s.Rebase)
                (ctx, triggers, ramAddresses, ramRebased, ramSilent) = RebaseOnSilence(ctx, triggers, ramAddresses, idleState);
            Addresses = ramAddresses;
            Rebased = ramRebased;
        }

        // Games that request sounds via a subroutine (Kirby's Adventure $F859,
        // Mega Man 2 $C051, Sunsoft's Journey to Silius $EA67 / Mr. Gimmick
        // $F2A3) often expose hundreds of channel-state false positives in RAM
        // that cannot silence the playing music. Probe sound routines before
        // running an expensive differential capture on unsilenced RAM triggers.
        RoutineProbe? routineProbe = null;
        var triedRoutines = false;
        if (queues is null && (triggers.Count == 0 || s.Rebase && !ramSilent))
        {
            triedRoutines = true;
            routineProbe = ProbeRoutines(apuPcs, minSp, maxSp, candidates, driverBanks, idleState);
            if (routineProbe is { } p && (triggers.Count == 0 || p.Silent))
            {
                var callSounds = CaptureRoutines(p);
                if (triggers.Count == 0 || callSounds.Count(x => x.Kind is "music" or "jingle") >= 2)
                {
                    if (triggers.Count > 0)
                        log("using silenced sound routine; skipping unsilenced RAM triggers");
                    Addresses = p.Addresses;
                    Rebased = p.Rebased;
                    return callSounds;
                }
                routineProbe = p with { Sounds = callSounds };
            }
        }

        var sounds = new List<Sound>();
        if (triggers.Count > 0)
        {
            if (triedRoutines)
                ctx = Applied(ctx);
            ctx = Applied(ctx with { Frozen = local.GameOwnedRam() });
            log($"freezing {ctx.Frozen.Count} bytes of game state during captures");
            sounds = Capture(ctx, triggers);
        }

        // Even when RAM was silent or rebased, a false mode/pause flag in RAM
        // (Mr. Gimmick $0071 re-playing the title theme as 4 jingles with 0
        // looping music) or an SFX-only queue can hide a real sound routine, so
        // fall back to routines whenever RAM found fewer than two looping music
        // tracks.
        if (queues is null && sounds.Count(x => x.Kind == "music") < 2)
        {
            if (!triedRoutines)
                routineProbe = ProbeRoutines(apuPcs, minSp, maxSp, candidates, driverBanks, idleState);
            if (routineProbe is { } p)
            {
                var callSounds = p.Sounds ?? CaptureRoutines(p);
                var callMusical = callSounds.Count(x => x.Kind is "music" or "jingle");
                var ramMusical = sounds.Count(x => x.Kind is "music" or "jingle");
                var callMusic = callSounds.Count(x => x.Kind == "music");
                var ramMusic = sounds.Count(x => x.Kind == "music");
                if (callMusical > ramMusical
                    || callMusical == ramMusical && (callMusic > ramMusic || callMusic == ramMusic && callSounds.Count > sounds.Count))
                {
                    Addresses = p.Addresses;
                    Rebased = p.Rebased;
                    return callSounds;
                }
            }
        }
        return sounds;
    }

    private sealed record RoutineProbe(
        ProbeContext Ctx, List<AddressReport> Triggers, List<AddressReport> Addresses,
        string? Rebased, bool Silent, List<Sound>? Sounds = null);

    private RoutineProbe? ProbeRoutines(ushort[] apuPcs, int minSp, int maxSp, List<ushort> candidates,
        DriverBank[]? driverBanks, object? idleState)
    {
        var routines = local.Machine.RoutineCandidates();
        if (routines.Count == 0)
            return null;
        log($"probing {routines.Count} sound routine candidates");
        var callCtx = Applied(new ProbeContext(apuPcs, minSp, maxSp, Array.Empty<ushort>(), null, new(), 0,
            CallMode: true, DriverReads: candidates.ToArray(), DriverBanks: driverBanks));
        var callAddresses = Sweep(callCtx, routines);
        var callTriggers = callAddresses.Where(a => a.IsTrigger).ToList();
        log($"routine triggers: {string.Join(", ", callTriggers.Select(t => $"{t.Address:X4} ({t.Responses.Count} values)"))}");
        if (callTriggers.Count == 0)
            return null;
        callCtx = Applied(callCtx with { Guarded = callTriggers.Select(t => t.Address).ToArray() });
        string? callRebased = null;
        var callSilent = false;
        if (s.Rebase)
            (callCtx, callTriggers, callAddresses, callRebased, callSilent) = RebaseOnSilence(callCtx, callTriggers, callAddresses, idleState);
        return new RoutineProbe(callCtx, callTriggers, callAddresses, callRebased, callSilent);
    }

    private List<Sound> CaptureRoutines(RoutineProbe probe)
    {
        var callCtx = Applied(probe.Ctx);
        callCtx = Applied(callCtx with { Frozen = local.GameOwnedRam() });
        log($"freezing {callCtx.Frozen.Count} bytes of game state during captures");
        return Capture(callCtx, probe.Triggers);
    }

    // Applies the context locally and stamps it with the base checksum the
    // workers must reproduce.
    private ProbeContext Applied(ProbeContext ctx)
    {
        var applied = ctx with { BaseChecksum = 0 };
        return applied with { BaseChecksum = local.Apply(applied) };
    }

    private List<AddressReport> Sweep(ProbeContext ctx, IReadOnlyList<ushort> addresses)
    {
        var done = 0;
        return pool.Map("sweep", ctx, addresses.Select(a => (JsonNode?)a).ToList(), (_, _) =>
            {
                if (++done % 64 == 0) log($"  probed {done}/{addresses.Count}");
            })
            .Select(n => Wire.DecodeAddress(n!)).ToList();
    }

    // Most drivers have a "stop" command: capturing on a silent chip keeps
    // effects and jingles free of music. Garbage requests can also silence
    // the chip by hanging the driver, and a "pause" flag silences it for
    // good, so a silence only counts if the triggers still respond on it and
    // some request can still make it play on. Drivers without a stop command
    // are silenced by putting their RAM back as it was while idle.
    private (ProbeContext Ctx, List<AddressReport> Triggers, List<AddressReport> Addresses, string? Rebased, bool Silent) RebaseOnSilence(
        ProbeContext ctx, List<AddressReport> triggers, List<AddressReport> addresses, object? idleState)
    {
        if (local.IsSilentAfter(null))
        {
            log("base state is already silent");
            return (ctx, triggers, addresses, null, true);
        }

        var origByAddr = triggers.ToDictionary(t => t.Address, t => t.Responses.Count);
        var distinct = triggers.SelectMany(t => t.Responses.GroupBy(r => r.Signature)
                .Select(g => (t.Address, g.OrderBy(r => StopPriority(r.Value)).First().Value)))
            .ToList();

        (ProbeContext Ctx, List<AddressReport> Triggers, List<AddressReport> Addresses, string? Rebased, bool Silent)? TrySilencers(
            IEnumerable<(string Name, IReadOnlyList<(ushort Address, byte Value)> Pokes, ushort? Source)> candidates)
        {
            foreach (var (name, pokes, source) in candidates)
            {
                var rebased = Applied(ctx with { Rebase = pokes });
                if (source is null && !(bool)pool.Map("silent", rebased, new JsonNode?[] { null })[0]!)
                {
                    log($"  {name} does not silence the music; skipped");
                    continue;
                }
                var resweep = Sweep(rebased, rebased.Guarded);
                // If a ring-buffer queue advanced its read index when consuming the
                // stop command (DuckTales $C8-$CF), its dispatcher now reads the
                // next slot: probe any new bytes read near the trigger consumers.
                if (!rebased.CallMode && resweep.Any(r => r.Responses.Count * 2 < origByAddr[r.Address]))
                {
                    var consumerPcs = (rebased.Consumers ?? Array.Empty<ConsumerRule>()).Select(r => r.Pc).ToArray();
                    var extra = local.ReadsNear(consumerPcs)
                        .Where(a => !rebased.Guarded.Contains(a)).ToList();
                    if (extra.Count > 0)
                        resweep.AddRange(Sweep(rebased, extra));
                }

                var surviving = resweep.Where(a => a.IsTrigger).ToList();
                if (!DriverAlive(source, triggers, surviving))
                {
                    log($"  {name} silences by breaking the driver " +
                        $"({string.Join(", ", resweep.Select(r => $"{r.Address:X4}: {r.Responses.Count}"))}); skipped");
                    continue;
                }

                // Keep the rebased-poke addresses guarded alongside the
                // surviving triggers so rebuilding the base state runs under
                // the same guard on the stop byte, plus the new slot's rules.
                var guarded = surviving.Select(a => a.Address).Concat(pokes.Select(p => p.Address)).Distinct().Order().ToArray();
                var consumers = (ctx.Consumers ?? Array.Empty<ConsumerRule>())
                    .Where(r => guarded.Contains(r.Address))
                    .Concat(ConsumersOf(surviving))
                    .Distinct().ToList();
                var prevChecksum = rebased.BaseChecksum;
                rebased = Applied(rebased with { Guarded = guarded, Consumers = consumers });
                if (rebased.BaseChecksum != prevChecksum)
                    surviving = Sweep(rebased, surviving.Select(a => a.Address).ToList()).Where(a => a.IsTrigger).ToList();
                var sample = SampleOf(surviving);
                var stillPlaying = pool.Map("silent", rebased, sample).Count(n => !(bool)n!);
                if (sample.Count > 0 && stillPlaying * 10 < sample.Count)
                {
                    log($"  {name} silences everything that follows (a pause flag?); skipped");
                    continue;
                }

                log($"rebased on silence: {name}");
                var reswept = resweep.Select(a => a.Address).ToHashSet();
                var updated = resweep.Concat(addresses.Where(a => !reswept.Contains(a.Address))).ToList();
                return (rebased, surviving, updated, name, true);
            }
            return null;
        }

        List<(string, IReadOnlyList<(ushort, byte)>, ushort?)> SilencersIn(List<(ushort Address, byte Value)> batch, int limit)
        {
            if (batch.Count == 0 || limit <= 0) return new();
            var silent = pool.Map("silent", ctx, batch.Select(t => (JsonNode?)ProberOps.Request(t.Address, t.Value)).ToList());
            // $FF is the usual "stop" value, so it is tried first; spread across
            // triggers so false channel-state triggers cannot crowd out the queue.
            return batch.Where((_, i) => (bool)silent[i]!)
                .GroupBy(t => t.Address)
                .SelectMany(g => g.OrderBy(t => StopPriority(t.Value)).Select((t, rank) => (Test: t, Rank: rank)))
                .OrderBy(x => StopPriority(x.Test.Value)).ThenBy(x => x.Rank).Take(limit)
                .Select(x => ($"{x.Test.Address:X4} = {x.Test.Value:X2}",
                    (IReadOnlyList<(ushort, byte)>)new[] { x.Test }, (ushort?)x.Test.Address))
                .ToList();
        }

        // Try common stop commands first ($FF/$FE/$80/$00) before testing other
        // values; cap the remaining silence probes so hundreds of channel-state
        // false triggers (Journey to Silius, Mr. Gimmick, Shatterhand) do not
        // spend minutes proving none of their values stop the music.
        const int maxSingleByteSilencers = 6;
        const int maxSlowSilenceProbes = 64;
        var commonStops = distinct.Where(t => StopPriority(t.Value) < 4).ToList();
        var fastCandidates = SilencersIn(commonStops, maxSingleByteSilencers);
        if (TrySilencers(fastCandidates) is { } fast)
            return fast;

        var rest = SampleForSilence(distinct.Where(t => StopPriority(t.Value) >= 4).ToList(), maxSlowSilenceProbes);
        var fallback = SilencersIn(rest, maxSingleByteSilencers - fastCandidates.Count);
        if (!ctx.CallMode)
        {
            var driverRam = local.DriverRam();
            if (driverRam.Count > 0)
            {
                if (idleState is not null)
                    fallback.Add(("the driver's idle RAM", local.ValuesIn(idleState, driverRam), null));
                fallback.Add(("the driver's RAM cleared", driverRam.Select(a => (a, (byte)0)).ToList(), null));
            }
        }
        if (TrySilencers(fallback) is { } slow)
            return slow;

        log("nothing silences the music; effects are isolated by differential capture");
        return (Applied(ctx with { Rebase = null }), triggers, addresses, null, false);
    }

    private static List<(ushort Address, byte Value)> SampleForSilence(List<(ushort Address, byte Value)> rest, int limit)
    {
        if (rest.Count <= limit)
            return rest;
        return rest
            .GroupBy(t => t.Address)
            .SelectMany(g =>
            {
                var count = g.Count();
                return g.OrderBy(t => SecondaryStopPriority(t.Value)).ThenBy(t => t.Value)
                    .Select((t, rank) => (Test: t, Rank: rank, Count: count));
            })
            .OrderBy(x => x.Rank).ThenBy(x => x.Count).ThenBy(x => x.Test.Address)
            .Take(limit)
            .Select(x => x.Test)
            .ToList();
    }

    private static int SecondaryStopPriority(byte value) => value switch
    {
        0xFD or 0xFC or 0xF0 or 0x7F or 0x7E => 0,
        0x01 or 0x02 or 0x04 or 0x08 or 0x10 or 0x20 or 0x40 => 1,
        _ => 2,
    };

    private static int StopPriority(byte value) => value switch
    {
        0xFF => 0,
        0xFE => 1,
        0x80 => 2,
        0x00 => 3,
        _ => 4,
    };

    // Silencing music also silences false channel-state triggers that only
    // responded while music played (DuckTales $037A..$03D7), so for a
    // single-byte stop command the driver is alive if the silencing trigger
    // (or its next ring-buffer slot) keeps responding. Multi-byte RAM resets
    // keep the stricter total-response check.
    private static bool DriverAlive(ushort? source, List<AddressReport> original, List<AddressReport> surviving)
    {
        if (surviving.Count == 0)
            return false;
        if (surviving.Sum(a => a.Responses.Count) * 2 >= original.Sum(a => a.Responses.Count))
            return true;
        if (source is not { } addr)
            return false;
        var originalAddresses = original.Select(t => t.Address).ToHashSet();
        var orig = original.First(t => t.Address == addr);
        bool Matches(AddressReport next) =>
            next.Address == orig.Address
            || !originalAddresses.Contains(next.Address) && orig.Consumers.Intersect(next.Consumers).Any();
        return surviving.Where(Matches).Sum(s => s.Responses.Count) * 2 >= orig.Responses.Count;
    }

    private static List<ConsumerRule> ConsumersOf(IEnumerable<AddressReport> triggers) =>
        triggers.SelectMany(t => t.Consumers.Select(pc => new ConsumerRule(t.Address, pc, t.BaseValue)))
            .Distinct().ToList();

    private static List<JsonNode?> SampleOf(List<AddressReport> triggers)
    {
        var all = triggers.SelectMany(t => t.Responses.Select(r => (t.Address, r.Value))).ToList();
        return all.Where((_, i) => i % Math.Max(1, all.Count / 32) == 0)
            .Select(t => (JsonNode?)ProberOps.Request(t.Address, t.Value)).ToList();
    }

    private List<Sound> Capture(ProbeContext ctx, List<AddressReport> triggers)
    {
        // Values of a byte that start out exactly alike are one sound reached
        // through an alias table (or the same garbage): capture one of them.
        var groups = triggers.SelectMany(t => t.Responses.GroupBy(r => r.Signature)
                .Select(g => (t.Address, Values: g.Select(r => r.Value).ToList())))
            .ToList();
        var items = groups.Select(g => (JsonNode?)ProberOps.Request(g.Address, g.Values[0])).ToList();
        log($"capturing {items.Count} distinct requests of {triggers.Sum(t => t.Responses.Count)}");
        var fps = local.Machine.Chip.FramesPerSecond;
        var results = pool.Map("capture", ctx, items, (i, n) =>
        {
            if (n == null)
                log($"  {items[i]![0]}={items[i]![1]}: crashed");
            else if (Wire.DecodeSound(n) is { Kind: not "none" } sound)
                log($"  {sound.Id}: {sound.Kind,-6} {sound.Frames / fps,6:F2}s  {sound.End}" +
                    (sound.LoopFrames is { } lf ? $" (loop {lf / fps:F1}s)" : ""));
        });

        // Keep the first request of every distinct sound; later ones are aliases.
        var sounds = new List<Sound>();
        var byHash = new Dictionary<ulong, Sound>();
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i] is not { } node || Wire.DecodeSound(node) is not { Kind: not "none", Writes.Count: > 0 } sound)
                continue;
            var aliases = groups[i].Values.Skip(1).Select(v => $"{groups[i].Address:X4}_{v:X2}");
            if (byHash.TryGetValue(sound.Hash, out var original))
            {
                original.Aliases.Add(sound.Id);
                original.Aliases.AddRange(aliases);
                continue;
            }
            sound.Aliases.AddRange(aliases);
            byHash[sound.Hash] = sound;
            sounds.Add(sound);
        }
        return sounds;
    }
}
