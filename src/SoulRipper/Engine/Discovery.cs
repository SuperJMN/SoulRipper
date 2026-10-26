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
        var (apuPcs, minSp, maxSp, candidates) = analysis ?? local.Analyze();
        ApuWriterPcs = apuPcs;
        if (apuPcs.Length == 0)
            log("warning: the driver wrote no sound registers during analysis; probing all RAM it touched");
        candidates = queues?.ToList() ?? candidates.Union(extra ?? Array.Empty<ushort>()).ToList();
        CandidateCount = candidates.Count;
        log($"driver: {apuPcs.Length} sound-writing PCs ({string.Join(" ", apuPcs.Take(8).Select(p => p.ToString("X4")))}), " +
            $"{candidates.Count} candidate bytes");

        var ctx = Applied(new ProbeContext(apuPcs, minSp, maxSp, Array.Empty<ushort>(), null, new(), 0));
        Addresses = Sweep(ctx, candidates);
        var triggers = Addresses.Where(a => a.IsTrigger).ToList();
        log($"triggers: {string.Join(", ", triggers.Select(t => $"{t.Address:X4} ({t.Responses.Count} values)"))}");
        if (triggers.Count == 0)
            return new List<Sound>();

        ctx = Applied(ctx with { Guarded = triggers.Select(t => t.Address).ToArray() });
        if (s.Rebase)
            (ctx, triggers) = RebaseOnSilence(ctx, triggers, idleState);

        ctx = Applied(ctx with { Frozen = local.GameOwnedRam() });
        log($"freezing {ctx.Frozen.Count} bytes of game state during captures");
        return Capture(ctx, triggers);
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
    private (ProbeContext, List<AddressReport>) RebaseOnSilence(ProbeContext ctx, List<AddressReport> triggers, object? idleState)
    {
        if (local.IsSilentAfter(null))
        {
            log("base state is already silent");
            return (ctx, triggers);
        }

        var tests = triggers.SelectMany(t => t.Responses.Select(r => (t.Address, r.Value))).ToList();
        var silent = pool.Map("silent", ctx, tests.Select(t => (JsonNode?)ProberOps.Request(t.Address, t.Value)).ToList());
        var responding = triggers.Sum(t => t.Responses.Count);
        var playing = tests.Where((_, i) => !(bool)silent[i]!).ToList();
        var sample = playing.Where((_, i) => i % Math.Max(1, playing.Count / 32) == 0)
            .Select(t => (JsonNode?)ProberOps.Request(t.Address, t.Value)).ToList();

        // $FF is the usual "stop" value, so it is tried first.
        var silencers = tests.Where((_, i) => (bool)silent[i]!).OrderBy(t => t.Value != 0xFF).Take(6)
            .Select(t => (Name: $"{t.Address:X4} = {t.Value:X2}", Pokes: (IReadOnlyList<(ushort, byte)>)new[] { t }))
            .ToList();
        var driverRam = local.DriverRam();
        if (idleState is not null)
            silencers.Add(("the driver's idle RAM", local.ValuesIn(idleState, driverRam)));
        silencers.Add(("the driver's RAM cleared", driverRam.Select(a => (a, (byte)0)).ToList()));

        foreach (var (name, pokes) in silencers)
        {
            var rebased = Applied(ctx with { Rebase = pokes });
            if (pokes.Count > 1 && !(bool)pool.Map("silent", rebased, new JsonNode?[] { null })[0]!)
            {
                log($"  {name} does not silence the music; skipped");
                continue;
            }
            var resweep = Sweep(rebased, rebased.Guarded);
            if (resweep.Sum(a => a.Responses.Count) * 2 < responding)
            {
                log($"  {name} silences by breaking the driver " +
                    $"({string.Join(", ", resweep.Select(r => $"{r.Address:X4}: {r.Responses.Count}"))}); skipped");
                continue;
            }
            var stillPlaying = pool.Map("silent", rebased, sample).Count(n => !(bool)n!);
            if (sample.Count > 0 && stillPlaying * 10 < sample.Count)
            {
                log($"  {name} silences everything that follows (a pause flag?); skipped");
                continue;
            }

            Rebased = name;
            log($"rebased on silence: {name}");
            Addresses = resweep.Concat(Addresses.Where(a => !rebased.Guarded.Contains(a.Address))).ToList();
            return (rebased, resweep.Where(a => a.Responses.Count > 0).ToList());
        }

        log("nothing silences the music; effects are isolated by differential capture");
        return (Applied(ctx with { Rebase = null }), triggers);
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
