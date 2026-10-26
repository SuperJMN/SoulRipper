namespace SoulRipper.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"soulripper-profile-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(path);

    [Fact]
    public void Curated_names_are_keyed_by_request_whatever_the_number_format()
    {
        File.WriteAllText(path, """
            {
              "name": "Game",
              "reach": { "boot": 240, "presses": [ { "buttons": ["Start"], "hold": 8 } ], "settle": 40 },
              "music": [ { "name": "Overworld", "id": "Overworld", "queue": "0xDFE8", "value": 7 } ],
              "sfx": [ { "name": "Jump", "id": "Player_Jump", "queue": 1265, "value": 1 },
                       { "name": "04F1_02", "id": "04F1_02", "queue": "0x04F1", "value": 2 } ]
            }
            """);

        var profile = Profile.Load(path);

        Assert.Equal(new Dictionary<string, string> { ["DFE8_07"] = "Overworld", ["04F1_01"] = "Jump" }, profile.Names());
        Assert.Equal(new ushort[] { 0xDFE8, 0x04F1 }, profile.Queues);
        Assert.Equal(240, profile.Reach!.Boot);
    }

    [Fact]
    public void Old_profiles_reach_their_state_by_booting()
    {
        File.WriteAllText(path, """{ "name": "Game", "boot_frames": 181, "sfx": [] }""");

        Assert.Equal(181, Profile.Load(path).Reach!.Boot);
    }
}
