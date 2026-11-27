using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SoulRipper;

// Runs prober operations over many items, in order of the items.
internal interface IWorkPool : IDisposable
{
    List<JsonNode?> Map(string op, ProbeContext ctx, IReadOnlyList<JsonNode?> items, Action<int, JsonNode?>? done = null);
}

internal static class ProberOps
{
    public static JsonNode? Execute(Prober p, string op, JsonNode? item) => op switch
    {
        "sweep" => Wire.Encode(p.Sweep(new[] { (ushort)(int)item! })[0]),
        "silent" => p.IsSilentAfter(item is null ? null : ((ushort)(int)item[0]!, (byte)(int)item[1]!)),
        "capture" => Capture(p, item!),
        _ => throw new ArgumentException($"unknown op: {op}"),
    };

    private static JsonNode? Capture(Prober p, JsonNode item)
    {
        try
        {
            return Wire.Encode(p.Capture((ushort)(int)item[0]!, (byte)(int)item[1]!));
        }
        catch (Exception)
        {
            return null;   // the request crashed the machine
        }
    }

    public static JsonNode Request(ushort address, byte value) => new JsonArray(address, value);
}

// Single-process pool on the orchestrator's own prober.
internal sealed class LocalPool(Prober prober) : IWorkPool
{
    private ProbeContext? applied;

    public List<JsonNode?> Map(string op, ProbeContext ctx, IReadOnlyList<JsonNode?> items, Action<int, JsonNode?>? done = null)
    {
        if (!ReferenceEquals(applied, ctx))
        {
            prober.Apply(ctx);
            applied = ctx;
        }
        var results = new List<JsonNode?>();
        for (var i = 0; i < items.Count; i++)
        {
            results.Add(Wired(ProberOps.Execute(prober, op, Wired(items[i]))));
            done?.Invoke(i, results[i]);
        }
        return results;
    }

    // Items and results go through JSON text as they do to and from a worker,
    // so both pools decode the same nodes: one built from a ushort does not
    // cast to int until it is parsed.
    private static JsonNode? Wired(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    public void Dispose()
    {
    }
}

// Pool of `soulripper worker` processes. Each one boots its own machine to the
// same reach state, so any of them can run any item; NES needs processes
// because the AprNes core is static.
internal sealed class ProcessPool : IWorkPool
{
    private const int MaxRespawns = 16;

    private readonly List<Worker> workers;
    private int respawns;

    public ProcessPool(int count, string rom, Reach reach, DiscoverySettings s, bool pal)
    {
        var reachJson = System.Text.Json.JsonSerializer.Serialize(reach.ToJson());
        workers = Enumerable.Range(0, count)
            .Select(_ => new Worker(this, rom, reachJson, s.MaxFrames, pal)).ToList();
    }

    public List<JsonNode?> Map(string op, ProbeContext ctx, IReadOnlyList<JsonNode?> items, Action<int, JsonNode?>? done = null)
    {
        var ctxJson = Wire.Encode(ctx).ToJsonString();
        var results = new JsonNode?[items.Count];
        var queue = new ConcurrentQueue<int>(Enumerable.Range(0, items.Count));
        var gate = new object();

        Parallel.ForEach(workers, new ParallelOptions { MaxDegreeOfParallelism = workers.Count }, worker =>
        {
            while (queue.TryDequeue(out var i))
            {
                var result = worker.Call(op, ctxJson, items[i]);
                results[i] = result;
                lock (gate) done?.Invoke(i, result);
            }
        });
        return results.ToList();
    }

    public void Dispose()
    {
        foreach (var w in workers) w.Dispose();
    }

    internal static JsonNode? CrashedResult(string op, JsonNode? item) => op switch
    {
        "capture" => null,
        "silent" => false,
        "sweep" => JsonNode.Parse(Wire.Encode(new AddressReport(
            (ushort)(int)JsonNode.Parse(item!.ToJsonString())!, 0, new())).ToJsonString()),
        _ => throw new InvalidOperationException($"worker died during {op}"),
    };

    private sealed class Worker : IDisposable
    {
        private readonly ProcessPool pool;
        private readonly string rom;
        private readonly string reachJson;
        private readonly int maxFrames;
        private readonly bool pal;
        private ConcurrentQueue<string> stderrTail = new();
        private Process process = null!;

        public Worker(ProcessPool pool, string rom, string reachJson, int maxFrames, bool pal)
        {
            this.pool = pool;
            this.rom = rom;
            this.reachJson = reachJson;
            this.maxFrames = maxFrames;
            this.pal = pal;
            Start();
        }

        private void Start()
        {
            var tail = new ConcurrentQueue<string>();
            stderrTail = tail;
            var dll = typeof(Program).Assembly.Location;
            var host = Environment.ProcessPath!;
            var info = new ProcessStartInfo(host)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (Path.GetFileNameWithoutExtension(host) == "dotnet")
                info.ArgumentList.Add(dll);
            foreach (var arg in new[] { "worker", "--rom", rom, "--reach", reachJson, "--max-frames", maxFrames.ToString() })
                info.ArgumentList.Add(arg);
            if (pal)
            {
                info.ArgumentList.Add("--region");
                info.ArgumentList.Add("pal");
            }

            process = Process.Start(info)!;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                tail.Enqueue(e.Data);
                while (tail.Count > 20) tail.TryDequeue(out string? _);
            };
            process.BeginErrorReadLine();
        }

        public JsonNode? Call(string op, string ctxJson, JsonNode? item)
        {
            var request = $"{{\"op\":\"{op}\",\"ctx\":{ctxJson},\"item\":{item?.ToJsonString() ?? "null"}}}";
            string? line;
            try
            {
                process.StandardInput.WriteLine(request);
                process.StandardInput.Flush();
                line = process.StandardOutput.ReadLine();
            }
            catch (IOException)
            {
                line = null;
            }
            if (line is null)
            {
                // Speculative probes can corrupt mapper state and trigger an
                // uncatchable native fault (e.g. AprNes Mapper001 out-of-range
                // PRG bank); replace the worker and treat that item as crashed.
                try { process.WaitForExit(500); } catch (InvalidOperationException) { }
                var diag = string.Join(" | ", stderrTail);
                DisposeProcess();
                if (Interlocked.Increment(ref pool.respawns) > MaxRespawns)
                    throw new InvalidOperationException("worker died: " + diag);
                Console.Error.WriteLine($"  worker crashed during {op} ({item?.ToJsonString() ?? "null"}); respawning");
                Start();
                return CrashedResult(op, item);
            }
            var response = JsonNode.Parse(line)!;
            if (!(bool)response["ok"]!)
                throw new InvalidOperationException("worker failed: " + (string?)response["error"]);
            return response["result"];
        }

        private void DisposeProcess()
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
            try
            {
                if (!process.WaitForExit(5000)) process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }

        public void Dispose() => DisposeProcess();
    }
}
