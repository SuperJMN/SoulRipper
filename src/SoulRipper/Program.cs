using System.Diagnostics;
using System.Text.Json.Nodes;
using SoulRipper.Hints;
using SoulRipper.Machines;
using SoulRipper.Output;

namespace SoulRipper;

internal static class Program
{
    private static readonly string[] Commands = { "rip", "discover", "bench", "access", "worker" };

    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt.Flag("help") || opt.Positional.Count == 0 && opt.Get("rom") is null)
            return Usage();

        var command = Commands.Contains(opt.Positional[0]) ? opt.Positional[0] : "rip";
        try
        {
            return command switch
            {
                "rip" => Rip(opt),
                "discover" => Discover(opt),
                "bench" => Bench(opt),
                "access" => Access(opt),
                "worker" => Worker(opt),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine($"soul-ripper: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            SoulRipper: rips the music, jingles and sound effects of a NES or Game Boy ROM to VGM.

            usage:
              soul-ripper <rom> [options]          rom: .nes, .gb, .gbc, or a zip/7z holding one

            options:
              --out DIR          output folder (default: the game's name)
              --profile P        curated profile: how to reach a playable state, sound names
              --boot N           frames to boot before probing (default 600)
              --max-seconds S    longest capture per sound (default 240)
              --keep-suspect     also write sounds past the game's valid IDs
              --offline          do not look the game up online
              --jobs N           worker processes (default: cores - 1)

            advanced:
              soul-ripper discover <rom> --out report.json [--queues A460,A468] [--extra 04F5] [--no-rebase]
              soul-ripper bench <rom> [--frames N]
              soul-ripper access <rom> --addr A460,A470 [--frames N]
            """);
        return 2;
    }

    // The ROM argument: the first positional after the command, or --rom.
    private static string RomArgument(Options opt)
    {
        var positional = opt.Positional.SkipWhile(p => Commands.Contains(p)).ToList();
        return opt.Get("rom") ?? positional.FirstOrDefault() ?? throw new ArgumentException("missing ROM");
    }

    private static int Rip(Options opt)
    {
        using var rom = RomFile.Open(RomArgument(opt));
        var profile = opt.Get("profile") is { } path ? Profile.Load(path) : Profile.Empty;
        var game = profile.Name ?? rom.Title();

        var hints = GameHints.None;
        if (!opt.Flag("offline"))
        {
            Console.WriteLine("Looking the game up online...");
            hints = GameHints.Lookup(rom.Path, rom.SystemId, Log);
            if (hints.Game is { } known && profile.Name is null)
                game = Titles.DisplayName(known);
        }

        var outDir = Path.GetFullPath(opt.Get("out") ?? RipWriter.SafeFileName(game));
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"{game} [{rom.SystemId}]: discovering sounds (this takes a few minutes)...");
        var sw = Stopwatch.StartNew();
        var (sounds, reach, fps, _) = RunDiscovery(rom.Path, opt, profile.Reach, null, hints.Queues.Concat(profile.Queues).Distinct().ToList());

        var names = hints.Names(sounds);
        if (names.Count > 0)
            Console.WriteLine($"Named {names.Count} sound(s) from the community rip");
        foreach (var (id, name) in profile.Names())
            names[id] = name;

        var rip = new RipResult(game, rom.SystemId, fps, reach, sounds, profile.Author, profile.Date);
        var entries = RipWriter.Write(rip, outDir, names, opt.Flag("keep-suspect"), hints.KnownRequests);
        PrintSummary(entries, outDir, sw.Elapsed);
        return 0;
    }

    private static void PrintSummary(List<RipEntry> entries, string outDir, TimeSpan elapsed)
    {
        var kept = entries.Where(e => e.File is not null).ToList();
        foreach (var e in kept)
            Console.WriteLine($"  {e.File,-48} {e.Seconds,7:F2}s{(e.LoopSeconds is { } l ? $"  loop {l:F1}s" : "")}");
        var counts = kept.GroupBy(e => e.Sound.Kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}");
        Console.WriteLine($"Done in {elapsed.TotalMinutes:F1} min: {string.Join(", ", counts)} in {outDir}");
        var skipped = entries.Count(e => e.File is null);
        if (skipped > 0)
            Console.WriteLine($"{skipped} suspect sound(s) past the valid ID range skipped (--keep-suspect writes them)");
    }

    private static int Discover(Options opt)
    {
        using var rom = RomFile.Open(RomArgument(opt));
        var sw = Stopwatch.StartNew();
        var reach = opt.Get("profile") is { } path ? Profile.Load(path).Reach : null;
        var (sounds, usedReach, _, discovery) = RunDiscovery(rom.Path, opt, reach,
            Options.Addresses(opt.Get("queues")), Options.Addresses(opt.Get("extra")) ?? new List<ushort>());
        using (var m = OpenMachine(rom.Path))
            Report.Write(opt.Req("out"), rom.Name, m, usedReach, discovery, sounds);
        Log($"{sounds.Count} sounds ({string.Join(", ", sounds.GroupBy(x => x.Kind).Select(g => $"{g.Count()} {g.Key}"))}) " +
            $"in {sw.Elapsed.TotalSeconds:F0}s -> {opt.Req("out")}");
        return 0;
    }

    private static (List<Sound> Sounds, Reach Reach, double Fps, Discovery Discovery) RunDiscovery(
        string rom, Options opt, Reach? reach, IReadOnlyList<ushort>? queues, IReadOnlyList<ushort> extra)
    {
        using var m = OpenMachine(rom);
        var (active, idle) = ReachFor(m, reach, opt.Int("boot", Reach.Default.Boot));
        var settings = new DiscoverySettings
        {
            MaxFrames = (int)(opt.Int("max-seconds", 240) * m.Chip.FramesPerSecond),
            Rebase = !opt.Flag("no-rebase"),
        };
        var jobs = opt.Int("jobs", Math.Clamp(Environment.ProcessorCount - 1, 1, 16));

        // A game that boots silent offers the cleanest base: the driver is
        // located while the game plays, and sounds are requested from the
        // silence, if the driver answers there.
        var activeState = m.SaveState();
        var attempts = new List<(Reach Reach, object State, Analysis? Analysis)>();
        if (idle is { } silent)
            attempts.Add((silent.Reach, silent.State, new Prober(m, activeState, settings).Analyze()));
        attempts.Add((active, activeState, null));

        foreach (var (attemptReach, state, analysis) in attempts)
        {
            m.LoadState(state);
            var prober = new Prober(m, state, settings);
            using IWorkPool pool = jobs > 1 ? new ProcessPool(jobs, rom, attemptReach, settings) : new LocalPool(prober);
            var discovery = new Discovery(prober, pool, settings, Log);
            var sounds = discovery.Run(queues, extra, analysis, idle?.State);
            if (sounds.Count > 0 || state == activeState)
                return (sounds, attemptReach, m.Chip.FramesPerSecond, discovery);
            Log("the driver does not answer while the game is silent; probing while it plays");
        }
        throw new InvalidOperationException("unreachable");
    }

    // Measures emulation speed and checks that savestates replay identically.
    private static int Bench(Options opt)
    {
        using var m = OpenMachine(RomArgument(opt));
        var frames = opt.Int("frames", 600);
        var apuWrites = 0;
        m.Write = (a, _, _) => { if (m.Chip.Owns(a)) apuWrites++; };

        var sw = Stopwatch.StartNew();
        m.RunFrames(frames);
        var fps = frames / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{m.Chip.Name}: {frames} frames in {sw.Elapsed.TotalSeconds:F2}s = {fps:F0} fps, {apuWrites} APU writes");

        sw.Restart();
        var state = m.SaveState();
        var saveMs = sw.Elapsed.TotalMilliseconds;
        var first = Record(m, 300);
        sw.Restart();
        m.LoadState(state);
        var loadMs = sw.Elapsed.TotalMilliseconds;
        var second = Record(m, 300);
        Console.WriteLine($"  savestate {saveMs:F1} ms, loadstate {loadMs:F1} ms, replay {(first.SequenceEqual(second) ? "IDENTICAL" : "DIVERGES")} ({first.Count} writes)");
        return 0;
    }

    // Serves prober operations for a ProcessPool: one JSON request per stdin
    // line, one JSON response per stdout line.
    private static int Worker(Options opt)
    {
        var protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(Console.Error);   // cores print diagnostics on stdout

        using var m = OpenMachine(opt.Req("rom"));
        using (var doc = System.Text.Json.JsonDocument.Parse(opt.Req("reach")))
            Reach.FromJson(doc.RootElement).Apply(m);
        var prober = new Prober(m, m.SaveState(), new DiscoverySettings { MaxFrames = opt.Int("max-frames", 60 * 240) });

        string? appliedCtx = null;
        while (Console.In.ReadLine() is { } line)
        {
            JsonObject response;
            try
            {
                var request = JsonNode.Parse(line)!;
                var ctxJson = request["ctx"]!.ToJsonString();
                if (ctxJson != appliedCtx)
                {
                    var ctx = Wire.DecodeContext(request["ctx"]!);
                    var checksum = prober.Apply(ctx);
                    if (checksum != ctx.BaseChecksum)
                        throw new InvalidOperationException("worker base state differs from the orchestrator's");
                    appliedCtx = ctxJson;
                }
                var result = ProberOps.Execute(prober, (string)request["op"]!, request["item"]);
                response = new JsonObject { ["ok"] = true, ["result"] = result };
            }
            catch (Exception ex)
            {
                appliedCtx = null;
                response = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
            protocol.WriteLine(response.ToJsonString());
        }
        return 0;
    }

    // Lists the instructions that read or write the given addresses.
    private static int Access(Options opt)
    {
        using var rom = RomFile.Open(RomArgument(opt));
        using var m = OpenMachine(rom.Path);
        _ = ReachFor(m, opt.Get("profile") is { } path ? Profile.Load(path).Reach : null, opt.Int("boot", Reach.Default.Boot));
        var targets = Options.Addresses(opt.Req("addr"))!.ToHashSet();
        var hits = new SortedDictionary<(ushort Address, string Kind, ushort Pc), int>();
        void Hit(ushort a, string kind, ushort pc) =>
            hits[(a, kind, pc)] = hits.GetValueOrDefault((a, kind, pc)) + 1;
        m.Read = (a, pc) => { if (targets.Contains(a)) Hit(a, "R", pc); };
        m.Write = (a, _, pc) =>
        {
            if (targets.Contains(a)) Hit(a, "W", pc);
            if (m.Chip.Owns(a)) Hit(0, "APU", pc);
        };
        m.RunFrames(opt.Int("frames", 60));
        foreach (var ((a, kind, pc), n) in hits)
            Console.WriteLine($"{(kind == "APU" ? "chip" : a.ToString("X4"))} {kind,-3} pc={pc:X4} x{n}");
        return 0;
    }

    // Uses the profile's reach when given; otherwise boots and, if the sound
    // driver is still idle, presses Start until it does something. The idle
    // state is returned too, to request sounds from silence.
    private static (Reach Active, (Reach Reach, object State)? Idle) ReachFor(IMachine m, Reach? fixedReach, int boot)
    {
        if (fixedReach is not null)
        {
            fixedReach.Apply(m);
            return (fixedReach, null);
        }

        var reach = Reach.Default with { Boot = boot };
        reach.Apply(m);
        (Reach, object)? idle = null;
        for (var attempt = 0; attempt < 3 && !DriverActive(m); attempt++)
        {
            Log("sound driver idle; pressing Start");
            idle ??= (reach, m.SaveState());
            m.Press(new[] { "start" }, 8);
            m.Press(Array.Empty<string>(), 240);
            reach = reach with { Presses = reach.Presses.Append((new[] { "start" }, 8)).Append((Array.Empty<string>(), 240)).ToList() };
        }
        return (reach, idle);
    }

    // Probing on a throwaway copy keeps the reach deterministic. Some drivers
    // write the same registers every frame while silent (SMB1 keeps the APU
    // off on its title screen), so only writes that change count.
    private static bool DriverActive(IMachine m)
    {
        var state = m.SaveState();
        var frames = Recorder.Record(m, 60).Frames;
        m.LoadState(state);
        var first = frames[0].ToHashSet();
        return frames.Any(f => !first.SetEquals(f));
    }

    private static void Log(string message) => Console.Error.WriteLine($"  {message}");

    private static List<(int, ushort, byte)> Record(IMachine m, int frames)
    {
        var writes = new List<(int, ushort, byte)>();
        var frame = 0;
        m.Write = (a, v, _) => { if (m.Chip.Owns(a)) writes.Add((frame, a, v)); };
        for (frame = 0; frame < frames; frame++)
            m.RunFrames(1);
        m.Write = null;
        return writes;
    }

    internal static IMachine OpenMachine(string rom) =>
        Path.GetExtension(rom).ToLowerInvariant() switch
        {
            ".nes" => new NesMachine(rom),
            ".gb" or ".gbc" => new GameBoyMachine(rom),
            var ext => throw new ArgumentException($"unsupported ROM type: {ext}"),
        };
}
