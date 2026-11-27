using System.Text.Json.Nodes;

namespace SoulRipper.Tests;

public class WorkPoolTests
{
    private static readonly ProbeContext Context =
        new(Array.Empty<ushort>(), 0xFFFF, 0, Array.Empty<ushort>(), null, new(), 0);

    [Fact]
    public void The_in_process_pool_sweeps_the_bytes_discovery_hands_it()
    {
        var machine = new FakeMachine();
        using var pool = new LocalPool(new Prober(machine, machine.SaveState(), new DiscoverySettings()));

        // Discovery hands over addresses as they are, as it does to workers.
        var results = pool.Map("sweep", Context, new JsonNode?[] { (ushort)0x0300 });

        Assert.Equal(0x0300, Wire.DecodeAddress(results[0]!).Address);
    }

    [Fact]
    public void Crashed_worker_fallbacks_decode_through_wire_for_every_operation()
    {
        // Discovery passes sweep items as raw ushort JsonNodes; the fallback
        // must survive the same Wire.DecodeAddress path as a real worker reply.
        var sweepNode = ProcessPool.CrashedResult("sweep", (JsonNode?)(ushort)0xC6ED);
        var report = Wire.DecodeAddress(sweepNode!);
        Assert.Equal(0xC6ED, report.Address);
        Assert.Empty(report.Responses);
        Assert.False(report.IsTrigger);

        var silentNode = ProcessPool.CrashedResult("silent", ProberOps.Request(0x0300, 0xFF));
        Assert.False((bool)silentNode!);

        var captureNode = ProcessPool.CrashedResult("capture", ProberOps.Request(0x0300, 0x01));
        Assert.Null(captureNode);
    }
}
