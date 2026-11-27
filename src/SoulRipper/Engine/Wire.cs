using System.Text.Json.Nodes;

namespace SoulRipper;

// JSON encoding of what crosses the process boundary between the discovery
// orchestrator and its workers.
internal static class Wire
{
    public static JsonObject Encode(ProbeContext c) => new()
    {
        ["apu_pcs"] = Ints(c.ApuPcs.Select(x => (int)x)),
        ["min_sp"] = c.MinSp,
        ["max_sp"] = c.MaxSp,
        ["guarded"] = Ints(c.Guarded.Select(x => (int)x)),
        ["consumers"] = new JsonArray((c.Consumers ?? Array.Empty<ConsumerRule>())
            .Select(r => (JsonNode)Ints(new[] { (int)r.Address, r.Pc, r.IdleValue })).ToArray()),
        ["call_mode"] = c.CallMode,
        ["driver_reads"] = Ints((c.DriverReads ?? Array.Empty<ushort>()).Select(x => (int)x)),
        ["rebase"] = c.Rebase is { } pokes
            ? new JsonArray(pokes.Select(p => (JsonNode)Ints(new[] { (int)p.Address, p.Value })).ToArray())
            : null,
        ["frozen"] = new JsonArray(c.Frozen.Select(kv => (JsonNode)Ints(new[] { (int)kv.Key, kv.Value })).ToArray()),
        ["checksum"] = c.BaseChecksum.ToString(),
    };

    public static ProbeContext DecodeContext(JsonNode n)
    {
        var rebase = n["rebase"]?.AsArray();
        var consumers = n["consumers"]?.AsArray()
            .Select(r => new ConsumerRule((ushort)(int)r![0]!, (ushort)(int)r[1]!, (byte)(int)r[2]!)).ToList();
        return new ProbeContext(
            UShorts(n["apu_pcs"]!),
            (int)n["min_sp"]!,
            (int)n["max_sp"]!,
            UShorts(n["guarded"]!),
            rebase?.Select(p => ((ushort)(int)p![0]!, (byte)(int)p[1]!)).ToList(),
            n["frozen"]!.AsArray().ToDictionary(p => (ushort)(int)p![0]!, p => (byte)(int)p![1]!),
            ulong.Parse((string)n["checksum"]!),
            consumers ?? new List<ConsumerRule>(),
            (bool?)n["call_mode"] ?? false,
            n["driver_reads"] is { } dr ? UShorts(dr) : Array.Empty<ushort>());
    }

    public static JsonObject Encode(AddressReport r) => new()
    {
        ["a"] = r.Address,
        ["base"] = r.BaseValue,
        ["r"] = new JsonArray(r.Responses.Select(x => (JsonNode)new JsonArray(
            x.Value, x.Signature.ToString(), x.Consumed, x.Restored)).ToArray()),
        ["c"] = Ints(r.Consumers.Select(x => (int)x)),
        ["call"] = r.IsCall,
    };

    public static AddressReport DecodeAddress(JsonNode n) => new(
        (ushort)(int)n["a"]!,
        (byte)(int)n["base"]!,
        n["r"]!.AsArray().Select(x => new Response(
            (byte)(int)x![0]!, ulong.Parse((string)x[1]!), (bool)x[2]!, (bool)x[3]!)).ToList(),
        n["c"] is { } c ? UShorts(c) : Array.Empty<ushort>(),
        (bool?)n["call"] ?? false);

    public static JsonObject Encode(Sound s) => new()
    {
        ["a"] = s.Address,
        ["v"] = s.Value,
        ["kind"] = s.Kind,
        ["end"] = s.End,
        ["frames"] = s.Frames,
        ["loop_start"] = s.LoopStart,
        ["loop_frames"] = s.LoopFrames,
        ["channels"] = Ints(s.Channels),
        ["hash"] = s.Hash.ToString(),
        ["init"] = new JsonArray(s.Init.Select(w => (JsonNode)Ints(new[] { (int)w.Address, w.Value })).ToArray()),
        ["writes"] = new JsonArray(s.Writes.Select(w => (JsonNode)Ints(new[] { w.Frame, w.Address, w.Value })).ToArray()),
        ["samples"] = new JsonArray((s.Samples ?? new()).Select(b =>
            (JsonNode)new JsonArray(b.Frame, b.Address, Convert.ToBase64String(b.Data))).ToArray()),
        ["call"] = s.IsCall,
    };

    public static Sound DecodeSound(JsonNode n) => new(
        (ushort)(int)n["a"]!,
        (byte)(int)n["v"]!,
        (string)n["kind"]!,
        (string)n["end"]!,
        (int)n["frames"]!,
        (int?)n["loop_start"],
        (int?)n["loop_frames"],
        n["channels"]!.AsArray().Select(x => (int)x!).ToArray(),
        ulong.Parse((string)n["hash"]!),
        n["init"]!.AsArray().Select(x => ((ushort)(int)x![0]!, (byte)(int)x[1]!)).ToList(),
        n["writes"]!.AsArray().Select(x => ((int)x![0]!, (ushort)(int)x[1]!, (byte)(int)x[2]!)).ToList(),
        n["samples"]?.AsArray().Select(x => ((int)x![0]!, (ushort)(int)x[1]!, Convert.FromBase64String((string)x[2]!))).ToList(),
        (bool?)n["call"] ?? false);

    private static JsonArray Ints(IEnumerable<int> values) => new(values.Select(v => (JsonNode)v).ToArray());

    private static ushort[] UShorts(JsonNode n) => n.AsArray().Select(x => (ushort)(int)x!).ToArray();
}
