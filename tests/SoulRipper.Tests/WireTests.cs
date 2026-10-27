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
}
