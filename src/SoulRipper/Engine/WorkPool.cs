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
            results.Add(ProberOps.Execute(prober, op, items[i]));
            done?.Invoke(i, results[i]);
        }
        return results;
    }

    public void Dispose()
    {
    }
}

// Pool of `soul-ripper worker` processes. Each one boots its own machine to the
// same reach state, so any of them can run any item; NES needs processes
// because the AprNes core is static.
internal sealed class ProcessPool : IWorkPool
{
    private readonly List<Worker> workers;

    public ProcessPool(int count, string rom, Reach reach, DiscoverySettings s)
    {
        var reachJson = System.Text.Json.JsonSerializer.Serialize(reach.ToJson());
        workers = Enumerable.Range(0, count)
            .Select(_ => new Worker(rom, reachJson, s.MaxFrames)).ToList();
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

    private sealed class Worker : IDisposable
    {
        private readonly Process process;
        private readonly ConcurrentQueue<string> stderrTail = new();

        public Worker(string rom, string reachJson, int maxFrames)
        {
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

            process = Process.Start(info)!;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stderrTail.Enqueue(e.Data);
                while (stderrTail.Count > 20) stderrTail.TryDequeue(out string? _);
            };
            process.BeginErrorReadLine();
        }

        public JsonNode? Call(string op, string ctxJson, JsonNode? item)
        {
            var request = $"{{\"op\":\"{op}\",\"ctx\":{ctxJson},\"item\":{item?.ToJsonString() ?? "null"}}}";
            process.StandardInput.WriteLine(request);
            process.StandardInput.Flush();
            var line = process.StandardOutput.ReadLine()
                       ?? throw new InvalidOperationException("worker died: " + string.Join(" | ", stderrTail));
            var response = JsonNode.Parse(line)!;
            if (!(bool)response["ok"]!)
                throw new InvalidOperationException("worker failed: " + (string?)response["error"]);
            return response["result"];
        }

        public void Dispose()
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000)) process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }
    }
}
