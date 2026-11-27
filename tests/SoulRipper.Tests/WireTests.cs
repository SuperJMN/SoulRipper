using System.Text.Json.Nodes;

namespace SoulRipper.Tests;

public class WireTests
{
    [Fact]
    public void Samples_reach_the_rip_from_worker_processes()
    {
        var sound = Sounds.Of(0x04F4, 1, "music", frames: 200, loopStart: 199) with
        {
            Samples = new() { (120, 0xEA80, new byte[] { 0x55, 0xD5, 0xAA }), (180, 0x8000, new byte[] { 0x12 }) },
        };

        // Workers answer with one JSON line per request.
        var decoded = Wire.DecodeSound(JsonNode.Parse(Wire.Encode(sound).ToJsonString())!);

        Assert.Equal(
            new[] { "120:EA80:55D5AA", "180:8000:12" },
            decoded.Samples!.Select(s => $"{s.Frame}:{s.Address:X4}:{Convert.ToHexString(s.Data)}"));
    }

    [Fact]
    public void Consumer_rules_cross_the_worker_boundary()
    {
        var rule = new ConsumerRule(0x00C9, 0xC11B, 0x88);
        var ctx = new ProbeContext(new ushort[] { 0x823F }, 0x01F0, 0x01FF, new ushort[] { 0x00C9 },
            null, new(), 12345UL, new[] { rule });
        var report = new AddressReport(0x00C9, 0x88, new() { new Response(0x01, 42UL, true, true) }, new ushort[] { 0xC11B });

        var decodedCtx = Wire.DecodeContext(JsonNode.Parse(Wire.Encode(ctx).ToJsonString())!);
        var decodedReport = Wire.DecodeAddress(JsonNode.Parse(Wire.Encode(report).ToJsonString())!);

        Assert.Equal(new[] { rule }, decodedCtx.Consumers);
        Assert.Equal(new ushort[] { 0xC11B }, decodedReport.Consumers);
    }

    [Fact]
    public void Wire_decodes_older_payloads_without_consumer_fields()
    {
        var ctxJson = JsonNode.Parse(
            """{"apu_pcs":[33343],"min_sp":496,"max_sp":511,"guarded":[201],"rebase":null,"frozen":[],"checksum":"0"}""")!;
        var addrJson = JsonNode.Parse("""{"a":201,"base":136,"r":[]}""")!;

        var ctx = Wire.DecodeContext(ctxJson);
        var report = Wire.DecodeAddress(addrJson);

        Assert.Empty(ctx.Consumers!);
        Assert.Empty(report.Consumers);
        Assert.False(ctx.CallMode);
        Assert.Empty(ctx.DriverReads!);
        Assert.False(report.IsCall);
    }

    [Fact]
    public void Call_mode_and_driver_reads_cross_the_worker_boundary()
    {
        var ctx = new ProbeContext(new ushort[] { 0x8155 }, 0x01F0, 0x01FF, new ushort[] { 0xF859 },
            null, new(), 99UL, CallMode: true, DriverReads: new ushort[] { 0x0600, 0x0066 });
        var report = new AddressReport(0xF859, 0, new() { new Response(0x00, 11UL, true, true) }, IsCall: true);

        var decodedCtx = Wire.DecodeContext(JsonNode.Parse(Wire.Encode(ctx).ToJsonString())!);
        var decodedReport = Wire.DecodeAddress(JsonNode.Parse(Wire.Encode(report).ToJsonString())!);

        Assert.True(decodedCtx.CallMode);
        Assert.Equal(new ushort[] { 0x0600, 0x0066 }, decodedCtx.DriverReads);
        Assert.True(decodedReport.IsCall);
        Assert.False(decodedReport.IsTrigger);

        var callSound = Sounds.Of(0xF859, 0x00, "music", frames: 200, loopStart: 100) with { IsCall = true };
        var decodedSound = Wire.DecodeSound(JsonNode.Parse(Wire.Encode(callSound).ToJsonString())!);
        Assert.True(decodedSound.IsCall);
    }
}
