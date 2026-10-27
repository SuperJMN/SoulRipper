using SoulRipper.Machines;

namespace SoulRipper;

internal sealed class DiscoverySettings
{
    public int AnalysisFrames { get; init; } = 90;
    public int ProbeFrames { get; init; } = 8;
    public int DriverWindow { get; init; } = 0x1000;
    public int MaxCandidates { get; init; } = 768;
    public int ReferenceFrames { get; init; } = 900;
    public int MaxFrames { get; init; } = 60 * 240;
    public int QuietFrames { get; init; } = 60;
    public int MusicMinFrames { get; init; } = 150;
    public bool Rebase { get; init; } = true;
}

// Where the sound driver is: the code that writes the sound chip, the stack
// range, and the RAM the driver reads (candidate request bytes).
internal sealed record Analysis(ushort[] ApuPcs, int MinSp, int MaxSp, List<ushort> Candidates);

internal sealed record Response(byte Value, ulong Signature, bool Consumed, bool Restored);

internal sealed record AddressReport(ushort Address, byte BaseValue, List<Response> Responses)
{
    public int Consumed => Responses.Count(r => r.Consumed);
    public int Restored => Responses.Count(r => r.Restored);
    public int Distinct => Responses.Select(r => r.Signature).Distinct().Count();

    // A sound request byte is read by the driver and then cleared or rewritten
    // by it, unlike channel state, which responds to pokes but keeps them.
    // Out-of-range IDs make many values respond alike, so only ratios count;
    // and a byte whose every value sounds the same is not choosing sounds.
    public bool IsTrigger => Responses.Count > 0 && Consumed * 2 >= Responses.Count && Restored * 4 >= Responses.Count
                             && (Distinct > 1 || Responses.Count == 1);
}

internal sealed record Sound(
    ushort Address, byte Value, string Kind, string End, int Frames, int? LoopStart, int? LoopFrames,
    int[] Channels, ulong Hash, List<(ushort Address, byte Value)> Init, List<(int Frame, ushort Address, byte Value)> Writes,
    List<(int Frame, ushort Address, byte[] Data)>? Samples = null)
{
    public string Id => $"{Address:X4}_{Value:X2}";
    public List<string> Aliases { get; } = new();

    // Identifies what a sound plays; requests with the same hash are aliases.
    public static ulong HashOf(int? loopStart, IEnumerable<(int Frame, ushort Address, byte Value)> writes,
        IEnumerable<(int Frame, ushort Address, byte[] Data)> samples)
    {
        var hash = 1469598103934665603UL ^ (ulong)(loopStart ?? -1);
        foreach (var (f, a, v) in writes)
            hash = (hash ^ ((ulong)f << 24 | (ulong)a << 8 | v)) * 1099511628211UL;
        // The same writes play other sounds if the DMC samples differ.
        foreach (var (f, a, data) in samples)
        {
            hash = (hash ^ ((ulong)f << 24 | (ulong)a << 8 | 0xC2)) * 1099511628211UL;
            foreach (var b in data)
                hash = (hash ^ b) * 1099511628211UL;
        }
        return hash;
    }
}

// Everything a prober needs to rebuild the same base state and run the same
// experiments as any other prober (in this or a worker process).
internal sealed record ProbeContext(
    ushort[] ApuPcs, int MinSp, int MaxSp, ushort[] Guarded,
    IReadOnlyList<(ushort Address, byte Value)>? Rebase, Dictionary<ushort, byte> Frozen, ulong BaseChecksum);

// Runs sound experiments on one machine: from a base state, poke a byte and
// watch what the sound chip does.
internal sealed class Prober
{
    private static readonly byte[] ProbeValues = { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 };

    private readonly IMachine m;
    private readonly object reachState;
    private readonly DiscoverySettings s;
    private object baseState;
    private ProbeContext ctx = new(Array.Empty<ushort>(), 0xFFFF, 0, Array.Empty<ushort>(), null, new(), 0);
    private HashSet<ushort> guarded = new();
    private readonly Dictionary<ushort, byte> guardValues = new();
    private (Timeline Timeline, ushort[] StateRam, bool Silent)? reference;
    private bool guarding;
    private bool poking;

    public Prober(IMachine m, object reachState, DiscoverySettings s)
    {
        this.m = m;
        this.reachState = reachState;
        baseState = reachState;
        this.s = s;
        m.Write = OnWrite;
        m.FrameEnded = OnFrameEnded;
    }

    public IMachine Machine => m;

    // Rebuilds the base state the context describes; returns its checksum.
    public ulong Apply(ProbeContext context)
    {
        // The frozen game state is measured on the rebased state, so rebasing
        // runs unfrozen.
        ctx = context with { Frozen = new() };
        guarded = context.Guarded.ToHashSet();
        reference = null;
        baseState = reachState;
        if (context.Rebase is { } pokes)
        {
            m.LoadState(reachState);
            foreach (var (address, value) in pokes)
                Poke(address, value);
            Isolated(() => m.RunFrames(120));
            baseState = m.SaveState();
        }
        ctx = context;
        m.LoadState(baseState);
        return Checksum();
    }

    // ---- analysis -----------------------------------------------------------
    // Locates the sound driver (code that writes the sound chip) and the RAM it
    // reads: the candidates for sound request bytes.
    public Analysis Analyze()
    {
        var pcs = new HashSet<ushort>();
        var readers = new Dictionary<ushort, HashSet<ushort>>();
        int minSp = 0xFFFF, maxSp = 0;

        m.LoadState(baseState);
        m.Write = (a, _, pc) =>
        {
            if (m.Chip.Owns(a)) pcs.Add(pc);
        };
        m.Read = (a, pc) =>
        {
            // Skip instruction fetches of code running from RAM.
            if (m.IsWorkRam(a) && (a < pc || a > pc + 3)) Add(readers, a, pc);
        };
        for (var f = 0; f < s.AnalysisFrames; f++)
        {
            m.RunFrames(1);
            minSp = Math.Min(minSp, m.StackPointer);
            maxSp = Math.Max(maxSp, m.StackPointer);
        }
        m.Read = null;
        m.Write = OnWrite;

        ctx = ctx with { ApuPcs = pcs.Order().ToArray(), MinSp = minSp, MaxSp = maxSp };
        var candidates = readers
            .Where(r => OffStack(r.Key) && (ctx.ApuPcs.Length == 0 || r.Value.Any(InDriver)))
            .Select(r => r.Key).Order().Take(s.MaxCandidates).ToList();
        return new Analysis(ctx.ApuPcs, minSp, maxSp, candidates);
    }

    private bool OffStack(ushort a) => a < ctx.MinSp - 0x40 || a > ctx.MaxSp + 0x08;

    private bool InDriver(ushort pc)
    {
        var pcs = ctx.ApuPcs;
        var i = Array.BinarySearch(pcs, pc);
        if (i >= 0) return true;
        i = ~i;
        return i < pcs.Length && pcs[i] - pc <= s.DriverWindow
               || i > 0 && pc - pcs[i - 1] <= s.DriverWindow;
    }

    private static void Add(Dictionary<ushort, HashSet<ushort>> map, ushort key, ushort pc)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<ushort>();
        if (set.Count < 64) set.Add(pc);
    }

    // ---- probe and sweep ------------------------------------------------------
    // Pokes a few values into each byte and, if the sound chip reacts, all 256.
    public List<AddressReport> Sweep(IReadOnlyList<ushort> candidates)
    {
        m.LoadState(baseState);
        var baseValues = candidates.ToDictionary(c => c, m.Peek);
        var refValues = candidates.ToDictionary(c => c, _ => new byte[s.ProbeFrames]);
        var probeReference = Recorder.Record(m, s.ProbeFrames, f =>
        {
            foreach (var c in candidates) refValues[c][f] = m.Peek(c);
            return true;
        });

        var reports = new List<AddressReport>();
        foreach (var c in candidates)
        {
            var baseValue = baseValues[c];
            var responses = ProbeValues.Where(v => v != baseValue)
                .Select(v => Probe(c, v, probeReference, refValues[c])).OfType<Response>().ToList();
            if (responses.Count > 0)
            {
                foreach (var v in Enumerable.Range(0, 256).Select(x => (byte)x))
                    if (v != baseValue && !ProbeValues.Contains(v) && (v != 0 || baseValue != 0)
                        && Probe(c, v, probeReference, refValues[c]) is { } r)
                        responses.Add(r);
                responses = responses.OrderBy(r => r.Value).ToList();
            }
            reports.Add(new AddressReport(c, baseValue, responses));
        }
        return reports;
    }

    private Response? Probe(ushort address, byte value, Timeline probeReference, byte[] refValues)
    {
        var values = new byte[s.ProbeFrames];
        Timeline t;
        try
        {
            m.LoadState(baseState);
            Poke(address, value);
            t = Recorder.Record(m, s.ProbeFrames, f =>
            {
                values[f] = m.Peek(address);
                return true;
            });
        }
        catch (Exception)
        {
            return null;   // the poke crashed the machine
        }

        var signature = 17UL;
        var differs = false;
        for (var f = 0; f < t.Count; f++)
        {
            var h = t.FrameHash(f);
            differs |= h != probeReference.FrameHash(f);
            signature = signature * 31 + h;
        }
        if (!differs)
            return null;

        var settle = Math.Min(4, values.Length);
        var consumed = values.Take(settle).Any(v => v != value);
        var restored = Enumerable.Range(0, settle).Any(f => values[f] != value && values[f] == refValues[f]);
        return new Response(value, signature, consumed, restored);
    }

    // Work RAM the driver reads or writes while it plays: its state. Code
    // near the driver also handles game state, so only the pages that hold
    // the request bytes count: drivers keep their state with them.
    public List<ushort> DriverRam()
    {
        var pages = guarded.Select(a => a >> 8).ToHashSet();
        var touched = new HashSet<ushort>();
        void Touch(ushort a, ushort pc)
        {
            if (pages.Contains(a >> 8) && m.IsWorkRam(a) && InDriver(pc) && OffStack(a) && (a < pc || a > pc + 3) && !guarded.Contains(a))
                touched.Add(a);
        }
        m.LoadState(baseState);
        m.Write = (a, _, pc) => Touch(a, pc);
        m.Read = Touch;
        m.RunFrames(120);
        m.Read = null;
        m.Write = OnWrite;
        m.LoadState(baseState);
        return touched.Order().ToList();
    }

    public List<(ushort, byte)> ValuesIn(object state, IEnumerable<ushort> addresses)
    {
        m.LoadState(state);
        var values = addresses.Select(a => (a, m.Peek(a))).ToList();
        m.LoadState(baseState);
        return values;
    }

    // ---- silence ----------------------------------------------------------------
    // Silent = from frame 60 to 180 every frame makes the same writes (often
    // none, or the same register refreshes). A request that silences the base is the driver's "stop" command.
    public bool IsSilentAfter((ushort Address, byte Value)? request)
    {
        const int from = 60, to = 180;
        try
        {
            m.LoadState(baseState);
            if (request is var (address, value)) Poke(address, value);
            var t = Isolated(() => Recorder.Record(m, to));
            // Drivers that run twice in some frames repeat their writes there.
            var first = t.Frames[from].ToHashSet();
            for (var f = from + 1; f < t.Count; f++)
                if (!first.SetEquals(t.Frames[f])) return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- isolation ------------------------------------------------------------
    // The game's clock is frozen for captures: the RAM that game code (not the
    // driver) writes is put back to its base value after every frame, so
    // attract-mode, level and death timers never fire while the driver plays.
    public Dictionary<ushort, byte> GameOwnedRam()
    {
        var gameOwned = new HashSet<ushort>();
        m.LoadState(baseState);
        m.Write = (a, _, pc) =>
        {
            if (m.IsWorkRam(a) && !InDriver(pc) && OffStack(a) && !guarded.Contains(a)) gameOwned.Add(a);
        };
        Isolated(() => m.RunFrames(300));
        m.Write = OnWrite;

        m.LoadState(baseState);
        return gameOwned.Order().ToDictionary(a => a, m.Peek);
    }

    // While isolated, the game's own writes to the request bytes are undone so
    // it cannot start other sounds, and its clock is frozen.
    private T Isolated<T>(Func<T> action)
    {
        guardValues.Clear();
        foreach (var a in guarded) guardValues[a] = m.Peek(a);
        guarding = true;
        try
        {
            return action();
        }
        finally
        {
            guarding = false;
        }
    }

    private void Isolated(Action action) => Isolated(() => { action(); return 0; });

    private void Poke(ushort address, byte value)
    {
        poking = true;
        m.Poke(address, value);
        poking = false;
        guardValues[address] = value;
    }

    private void OnFrameEnded()
    {
        if (!guarding || ctx.Frozen.Count == 0)
            return;
        poking = true;
        foreach (var (a, v) in ctx.Frozen)
            m.Poke(a, v);
        poking = false;
    }

    private void OnWrite(ushort address, byte value, ushort pc)
    {
        if (!guarding || poking || !guarded.Contains(address))
            return;
        if (InDriver(pc))
        {
            guardValues[address] = value;
            return;
        }
        poking = true;
        m.Poke(address, guardValues[address]);
        poking = false;
    }

    private ulong Checksum()
    {
        var h = 1469598103934665603UL;
        for (var a = 0; a < 0x10000; a++)
            if (m.IsWorkRam((ushort)a))
                h = (h ^ m.Peek((ushort)a)) * 1099511628211UL;
        return h;
    }

    // ---- capture --------------------------------------------------------------
    // The untriggered run, the RAM that stays static in it (state a triggered
    // sound owns, so a repeat of it marks a loop) and whether it is silent.
    private (Timeline Timeline, ushort[] StateRam, bool Silent) Reference()
    {
        if (reference is { } cached)
            return cached;

        m.LoadState(baseState);
        var ram = Enumerable.Range(0, 0x10000).Select(a => (ushort)a).Where(m.IsWorkRam).ToArray();
        var first = ram.Select(m.Peek).ToArray();
        var changed = new bool[ram.Length];
        var audible = false;
        var timeline = Isolated(() => Recorder.Record(m, s.ReferenceFrames, f =>
        {
            for (var i = 0; i < ram.Length; i++)
                changed[i] |= m.Peek(ram[i]) != first[i];
            audible |= f >= 30 && m.Audible;
            return true;
        }));
        reference = (timeline, ram.Where((_, i) => !changed[i]).ToArray(), !audible);
        return reference.Value;
    }

    // Records one request until its sound ends, loops or hits the time limit.
    public Sound Capture(ushort address, byte value)
    {
        var (refTimeline, stateRam, refSilent) = Reference();
        m.LoadState(baseState);
        var init = m.Chip.InitWrites(m.ApuShadow);
        Poke(address, value);

        var timeline = new Timeline();

        // DMC samples play from cartridge memory, which a VGM player lacks:
        // keep the bytes each sample start points at, as banked at that moment.
        // Which starts need loading depends on the loop, known only at the end.
        var played = new List<(int Frame, ushort Address, byte[] Data)>();
        var last = new Dictionary<ushort, byte[]>();
        var observer = m.Write;
        m.Write = (a, v, pc) =>
        {
            observer?.Invoke(a, v, pc);
            if (m.Chip.SampleStart(a, v, m.ApuShadow) is not { } sample)
                return;
            foreach (var (start, length) in SampleMemory.Ranges(sample.Address, sample.Length))
            {
                // Drivers restart the same sample often: share its bytes.
                var data = Read(start, length);
                if (last.TryGetValue(start, out var known) && known.AsSpan().SequenceEqual(data))
                    data = known;
                last[start] = data;
                played.Add((timeline.Count - 1, start, data));
            }
        };

        var diffs = new List<List<(ushort Address, byte Value)>>();
        var active = new List<int>();             // running count of frames with new writes
        var states = new Dictionary<(ulong, ulong), int>();
        var hashes = new List<ulong>();
        var lastActive = -1;
        var lastAudible = -1;
        var end = "max";
        int? loopStart = null, loopFrames = null;
        (int Start, int Length, int Until)? pending = null;   // loop awaiting confirmation

        // A request that crashes the machine must not leave the sample observer
        // chained for the next capture of this prober.
        try
        {
            Isolated(() => Recorder.Extend(m, timeline, s.MaxFrames, f =>
            {
                var diff = timeline.DiffFrame(f, refTimeline);
                diffs.Add(diff);
                hashes.Add(timeline.FrameHash(f));
                active.Add((f > 0 ? active[f - 1] : 0) + (diff.Count > 0 ? 1 : 0));
                if (refSilent && m.Audible)
                    lastAudible = f;
                if (diff.Count > 0)
                    lastActive = f;
                else if (lastActive < 0 && f >= 120)
                {
                    end = "silent";
                    return false;
                }
                // Over silence, the last notes ring out after the last write (a
                // Game Boy envelope fades on its own); a note that never fades is
                // cut after a while.
                else if (lastActive >= 0 && f - lastActive >= s.QuietFrames
                         && (f - lastAudible >= s.QuietFrames || f - lastActive >= 4 * s.QuietFrames))
                {
                    end = "ended";
                    return false;
                }

                // Once the sound's state repeats, so should its writes: a repeat is
                // confirmed by the writes matching one period back for a while.
                if (pending is var (ls, ll, until))
                {
                    if (hashes[f] != hashes[f - ll])
                        pending = null;
                    else if (f >= until)
                    {
                        loopStart = ls;
                        loopFrames = ll;
                        end = "loop";
                        return false;
                    }
                }
                else if (stateRam.Length > 0)
                {
                    var key = SoundState(stateRam);
                    if (!states.TryAdd(key, f) && active[f] - active[states[key]] > 0)
                    {
                        var length = f - states[key];
                        pending = (states[key] + 1, length, f + Math.Min(Math.Max(length, 120), 600));
                        states[key] = f;
                    }
                }

                // Fallback for drivers whose state never repeats exactly.
                if (pending == null && f >= 720 && f % 240 == 0 && FindPeriod(hashes, 120) is var (ps, pl)
                    && active[ps + pl - 1] - (ps > 0 ? active[ps - 1] : 0) > 0)
                {
                    loopStart = ps;
                    loopFrames = pl;
                    end = "loop";
                    return false;
                }
                return true;
            }));
        }
        finally
        {
            m.Write = observer;
        }

        var channels = diffs.SelectMany(d => d).Select(w => m.Chip.ChannelOf(w.Address))
            .Where(c => c >= 0).Distinct().Order().ToArray();
        var frames = end switch
        {
            "loop" => loopStart!.Value + loopFrames!.Value,
            "max" => timeline.Count,
            _ => lastActive + 1,
        };
        var musical = frames >= s.MusicMinFrames && channels.Length >= 2;
        if (end == "ended")
            frames = Math.Max(frames, Math.Min(lastAudible, lastActive + 4 * s.QuietFrames) + 1);
        // Over silence, a request that writes the chip but is never heard is
        // garbage the driver half-accepted.
        var kind = end switch
        {
            _ when refSilent && lastAudible < 0 => "none",
            "loop" or "max" => musical ? "music" : "sfx",
            "ended" => musical ? "jingle" : "sfx",
            _ => "none",
        };

        // Music replaces whatever played before, so it is kept whole; effects
        // keep only what they added on top of the reference.
        var writes = new List<(int, ushort, byte)>();
        for (var f = 0; f < frames; f++)
            foreach (var (a, v) in kind is "music" or "jingle" ? timeline.Frames[f] : diffs[f])
                writes.Add((f, a, v));

        var samples = SampleMemory.Blocks(played, frames, loopStart);
        var hash = Sound.HashOf(loopStart, writes, samples);

        return new Sound(address, value, kind, end, frames, loopStart, loopFrames, channels, hash, init, writes, samples);
    }

    private byte[] Read(ushort start, int length) =>
        Enumerable.Range(0, length).Select(i => m.Peek((ushort)(start + i))).ToArray();

    private (ulong, ulong) SoundState(ushort[] stateRam)
    {
        ulong h1 = 1469598103934665603UL, h2 = 0x9E3779B97F4A7C15UL;
        foreach (var a in stateRam)
        {
            var v = m.Peek(a);
            h1 = (h1 ^ v) * 1099511628211UL;
            h2 = (h2 + v + 1) * 0xBF58476D1CE4E5B9UL;
        }
        return (h1, h2);
    }

    // Smallest period repeated three times up to the last frame, and the
    // earliest frame the repetition holds from.
    private static (int Start, int Length)? FindPeriod(List<ulong> h, int minLength)
    {
        var n = h.Count;
        for (var p = minLength; p * 3 <= n; p++)
        {
            var ok = true;
            for (var i = n - 1; i >= n - 2 * p && ok; i--)
                ok = h[i] == h[i - p];
            if (!ok) continue;

            var start = n - 3 * p;
            while (start > 0 && h[start - 1] == h[start - 1 + p]) start--;
            return (start, p);
        }
        return null;
    }
}
