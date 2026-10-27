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
}
