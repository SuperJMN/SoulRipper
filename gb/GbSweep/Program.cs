using System.Globalization;
using System.Text;
using System.Text.Json;
using GameBoy.Debug.Core;
using GameBoy.Debug.Emulator;

namespace GbSweep;

// Captures Game Boy APU register writes ($FF10-$FF3F) to a text log that the
// Python VGM writer turns into per-effect VGM files. Uses the CoreBoy-based
// debug session (per-address last-writer tracking with cumulative counts).
internal static class Program
{
    private const int ApuBase = 0xFF10;
    private const int ApuLen = 0x30;          // $FF10-$FF3F (regs + wave RAM)

    private static int Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();

        var mode = args[0];
        var opt = ParseOptions(args);

        return mode switch
        {
            "record" => RunRecord(opt),
            "sweep" => RunSweep(opt),
            "finddiff" => RunFindDiff(opt),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage:\n" +
            "  gbsweep record   --rom game.gb --boot N --frames M --out capture.txt\n" +
            "  gbsweep sweep    --rom game.gb --profile p.json --out capture.txt\n" +
            "  gbsweep finddiff --rom game.gb --profile p.json --trigger down --frames N");
        return 2;
    }

    // ---- finddiff: locate a sound-request RAM byte by input differencing -----
    // The engine clears the request byte every frame, and the game sets it for
    // less than a frame, so we can't observe the value directly. Instead we diff
    // the per-address WRITE COUNT between an idle run and a triggered run: the
    // request byte (and a few input-state bytes) receive an extra write only when
    // the SFX is triggered.
    private static int RunFindDiff(Dictionary<string, string> opt)
    {
        var rom = Req(opt, "rom");
        var frames = int.Parse(opt.GetValueOrDefault("frames", "20"));
        var trigger = ParseButton(Req(opt, "trigger"));
        using var doc = JsonDocument.Parse(File.ReadAllText(Req(opt, "profile")));
        var reach = doc.RootElement.GetProperty("reach");

        var s = Load(rom);

        Reach(s, reach);
        var refDelta = CountDeltas(s, frames, press: null);

        Reach(s, reach);
        var testDelta = CountDeltas(s, frames, press: trigger);

        Console.Error.WriteLine($"[finddiff] addresses written MORE when '{opt["trigger"]}' pressed:");
        var hits = 0;
        for (var i = 0; i < WramLen; i++)
        {
            if (testDelta[i] > refDelta[i])
            {
                var addr = WramBase + i;
                var extra = testDelta[i] - refDelta[i];
                Console.Error.WriteLine($"  {addr:X4}  +{extra} writes (ref {refDelta[i]}, test {testDelta[i]})");
                hits++;
            }
        }
        if (hits == 0) Console.Error.WriteLine("  (none - input may not trigger a sound in this state)");
        return 0;
    }

    private const int WramBase = 0xC000;
    private const int WramLen = 0x2000;   // C000-DFFF

    private static ulong[] WramCounts(ManagedGameBoyDebugSession s)
    {
        var counts = new ulong[WramLen];
        for (var chunk = 0; chunk < WramLen; chunk += 0x1000)
        {
            var w = s.FindLastWriters((ushort)(WramBase + chunk), 0x1000).Value.Writers;
            for (var i = 0; i < 0x1000; i++) counts[chunk + i] = w[i].WriteCount;
        }
        return counts;
    }

    private static ulong[] CountDeltas(ManagedGameBoyDebugSession s, int frames, JoypadButton? press)
    {
        var start = WramCounts(s);
        if (press is JoypadButton b) s.PressButtons(new[] { b }, 2);
        else s.RunFrame(2);                 // keep both runs frame-aligned
        s.RunFrame(frames);
        var end = WramCounts(s);
        var d = new ulong[WramLen];
        for (var i = 0; i < WramLen; i++) d[i] = end[i] - start[i];
        return d;
    }

    // ---- record: dump all APU writes for M frames (pipeline validation) -----
    private static int RunRecord(Dictionary<string, string> opt)
    {
        var rom = Req(opt, "rom");
        var boot = int.Parse(opt.GetValueOrDefault("boot", "0"));
        var frames = int.Parse(opt.GetValueOrDefault("frames", "180"));
        var pressStart = opt.ContainsKey("press-start");
        var outPath = Req(opt, "out");

        var s = Load(rom);
        if (boot > 0) s.RunFrame(boot);
        if (pressStart) s.PressButtons(new[] { JoypadButton.Start }, 8);

        var sb = new StringBuilder();
        sb.AppendLine("SFX 1 record");
        var prev = Snapshot(s);                // baseline: ignore pre-capture writes
        for (var f = 0; f < frames; f++)
        {
            s.RunFrame(1);
            EmitFrameWrites(s, prev, f, sb);
        }
        sb.AppendLine("SFXEND");
        File.WriteAllText(outPath, sb.ToString());
        Console.Error.WriteLine($"[gbsweep] recorded {frames} frames -> {outPath}");
        return 0;
    }

    // ---- sweep: profile-driven, isolated per-effect capture ----------------
    // Uses DIFFERENTIAL capture: the same deterministic state is run twice per
    // effect - once as a music-only reference, once with the SFX request poked.
    // Only writes that the SFX changes are emitted, so concurrent music (which
    // is byte-identical in both runs) cancels out and the effect is isolated.
    private static int RunSweep(Dictionary<string, string> opt)
    {
        var rom = Req(opt, "rom");
        var outPath = Req(opt, "out");
        using var doc = JsonDocument.Parse(File.ReadAllText(Req(opt, "profile")));
        var p = doc.RootElement;

        var reach = p.GetProperty("reach");
        var captureFrames = p.TryGetProperty("capture_frames", out var cf) ? cf.GetInt32() : 90;
        var isolate = p.TryGetProperty("isolate", out var iso) ? iso.GetString() : "diff";
        var sfxList = p.GetProperty("sfx");

        var s = Load(rom);
        var sb = new StringBuilder();

        var idx = 0;
        foreach (var sfx in sfxList.EnumerateArray())
        {
            idx++;
            var id = sfx.GetProperty("id").GetString();
            var queue = ReadNum(sfx.GetProperty("queue"));
            var value = ReadNum(sfx.GetProperty("value"));

            List<(int f, int addr, int val)> reference = null;
            if (isolate == "diff")
            {
                Reach(s, reach);
                reference = CaptureRun(s, captureFrames, poke: null);
            }

            Reach(s, reach);
            var test = CaptureRun(s, captureFrames, poke: ((ushort)queue, (byte)value));

            var writes = isolate == "diff" ? DiffWrites(reference, test) : test;

            sb.AppendLine($"SFX {idx} {id}");
            foreach (var (f, addr, val) in writes)
                sb.AppendLine($"W {f} {addr:X4} {val:X2}");
            sb.AppendLine("SFXEND");
            Console.Error.WriteLine($"[gbsweep] [{idx:D2}] {id,-16} {writes.Count} writes");
        }

        File.WriteAllText(outPath, sb.ToString());
        Console.Error.WriteLine($"[gbsweep] swept {idx} effects -> {outPath}");
        return 0;
    }

    // Deterministically drive the machine to the capture state.
    private static void Reach(ManagedGameBoyDebugSession s, JsonElement reach)
    {
        s.Reset();
        if (reach.TryGetProperty("boot", out var b)) s.RunFrame(b.GetInt32());
        if (reach.TryGetProperty("presses", out var presses))
            foreach (var pr in presses.EnumerateArray())
            {
                var buttons = pr.GetProperty("buttons").EnumerateArray()
                    .Select(x => ParseButton(x.GetString())).ToArray();
                var hold = pr.TryGetProperty("hold", out var h) ? h.GetInt32() : 4;
                s.PressButtons(buttons, hold);
            }
        if (reach.TryGetProperty("settle", out var st)) s.RunFrame(st.GetInt32());
    }

    // Runs `frames` frames (optionally poking a byte at frame 0) and returns the
    // full per-frame APU write timeline via cumulative write-count diffing.
    private static List<(int f, int addr, int val)> CaptureRun(
        ManagedGameBoyDebugSession s, int frames, (ushort addr, byte val)? poke)
    {
        var prev = Snapshot(s);
        if (poke is var (pa, pv) && poke.HasValue)
            s.WriteMemory(pa, new byte[] { pv });

        var outl = new List<(int, int, int)>();
        for (var f = 0; f < frames; f++)
        {
            s.RunFrame(1);
            var w = s.FindLastWriters((ushort)ApuBase, ApuLen).Value.Writers;
            for (var i = 0; i < ApuLen; i++)
            {
                if (!w[i].Found || w[i].WriteCount <= prev[i]) continue;
                prev[i] = w[i].WriteCount;
                outl.Add((f, ApuBase + i, ParseByte(w[i].Value)));
            }
        }
        return outl;
    }

    // Emit only test writes that differ from the reference at the same
    // (frame, register). Identical music writes are dropped.
    private static List<(int f, int addr, int val)> DiffWrites(
        List<(int f, int addr, int val)> reference, List<(int f, int addr, int val)> test)
    {
        var refMap = new Dictionary<(int, int), int>();
        foreach (var (f, a, v) in reference) refMap[(f, a)] = v;
        var outl = new List<(int, int, int)>();
        foreach (var (f, a, v) in test)
            if (!refMap.TryGetValue((f, a), out var rv) || rv != v)
                outl.Add((f, a, v));
        return outl;
    }


    // ---- helpers -----------------------------------------------------------
    private static ManagedGameBoyDebugSession Load(string rom)
    {
        var s = new ManagedGameBoyDebugSession();
        var r = s.LoadRom(rom);
        if (!r.IsSuccess)
            throw new Exception($"load_rom failed: {r.Error!.Message}");
        return s;
    }

    private static ulong[] Snapshot(ManagedGameBoyDebugSession s)
    {
        var w = s.FindLastWriters((ushort)ApuBase, ApuLen).Value.Writers;
        var counts = new ulong[ApuLen];
        for (var i = 0; i < ApuLen; i++) counts[i] = w[i].WriteCount;
        return counts;
    }

    // Emits W lines for any APU register whose cumulative write count grew this
    // frame; returns true if anything meaningful (a channel register) changed.
    private static bool EmitFrameWrites(ManagedGameBoyDebugSession s, ulong[] prev, int frame, StringBuilder sb)
    {
        var w = s.FindLastWriters((ushort)ApuBase, ApuLen).Value.Writers;
        var meaningful = false;
        for (var i = 0; i < ApuLen; i++)
        {
            if (!w[i].Found || w[i].WriteCount <= prev[i]) continue;
            prev[i] = w[i].WriteCount;
            var addr = ApuBase + i;
            var val = ParseByte(w[i].Value);
            sb.AppendLine($"W {frame} {addr:X4} {val:X2}");
            if (addr <= 0xFF25) meaningful = true;   // NR10..NR51 channel activity
        }
        return meaningful;
    }

    private static byte ParseByte(string hex)
    {
        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return byte.Parse(hex, NumberStyles.HexNumber);
    }

    private static int ReadNum(JsonElement e) =>
        e.ValueKind == JsonValueKind.String
            ? Convert.ToInt32(e.GetString(), 16)
            : e.GetInt32();

    private static JoypadButton ParseButton(string name) => name.ToLowerInvariant() switch
    {
        "right" => JoypadButton.Right,
        "left" => JoypadButton.Left,
        "up" => JoypadButton.Up,
        "down" => JoypadButton.Down,
        "a" => JoypadButton.A,
        "b" => JoypadButton.B,
        "select" => JoypadButton.Select,
        "start" => JoypadButton.Start,
        _ => throw new Exception($"unknown button: {name}"),
    };

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            d[key] = val;
        }
        return d;
    }

    private static string Req(Dictionary<string, string> opt, string key) =>
        opt.TryGetValue(key, out var v) ? v : throw new Exception($"missing --{key}");
}
