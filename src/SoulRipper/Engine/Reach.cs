using System.Text.Json;
using SoulRipper.Machines;

namespace SoulRipper;

// How to drive a freshly booted machine to the state sounds are triggered from.
internal sealed record Reach(int Boot, IReadOnlyList<(string[] Buttons, int Hold)> Presses, int Settle)
{
    public static readonly Reach Default = new(600, Array.Empty<(string[], int)>(), 0);

    public void Apply(IMachine m)
    {
        m.RunFrames(Boot);
        foreach (var (buttons, hold) in Presses)
            m.Press(buttons, hold);
        m.RunFrames(Settle);
    }

    // Profile `reach` block: { "boot": N, "presses": [{ "buttons": [...], "hold": N }], "settle": N }
    public static Reach FromJson(JsonElement e) => new(
        e.TryGetProperty("boot", out var b) ? b.GetInt32() : 0,
        e.TryGetProperty("presses", out var p)
            ? p.EnumerateArray().Select(x => (
                x.GetProperty("buttons").EnumerateArray().Select(y => y.GetString()!).ToArray(),
                x.TryGetProperty("hold", out var h) ? h.GetInt32() : 4)).ToList()
            : Array.Empty<(string[], int)>(),
        e.TryGetProperty("settle", out var s) ? s.GetInt32() : 0);

    public object ToJson() => new
    {
        boot = Boot,
        presses = Presses.Select(p => new { buttons = p.Buttons, hold = p.Hold }),
        settle = Settle,
    };
}
