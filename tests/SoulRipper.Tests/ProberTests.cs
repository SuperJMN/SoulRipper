namespace SoulRipper.Tests;

public class ProberTests
{
    [Fact]
    public void A_crashed_capture_leaves_the_machine_observed_as_before()
    {
        var machine = new FakeMachine(crashAt: (0x0300, 0x01));
        var prober = new Prober(machine, machine.SaveState(), new DiscoverySettings());
        var observer = machine.Write;

        Assert.ThrowsAny<Exception>(() => prober.Capture(0x0300, 0x01));

        Assert.Same(observer, machine.Write);
    }
}
