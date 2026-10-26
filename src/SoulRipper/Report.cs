using System.Text.Json;
using SoulRipper.Machines;

namespace SoulRipper;

// Writes the raw discovery result as JSON (the `discover` command).
internal static class Report
{
    public static void Write(string path, string rom, IMachine m, Reach reach, Discovery d, List<Sound> sounds)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        w.WriteStartObject();
        w.WriteString("system", m.Chip.Name);
        w.WriteString("rom", Path.GetFileName(rom));
        w.WriteNumber("fps", m.Chip.FramesPerSecond);
        w.WritePropertyName("reach");
        JsonSerializer.Serialize(w, reach.ToJson());

        w.WriteStartObject("driver");
        w.WriteStartArray("apu_writer_pcs");
        foreach (var pc in d.ApuWriterPcs) w.WriteStringValue(Hex(pc));
        w.WriteEndArray();
        w.WriteNumber("candidates", d.CandidateCount);
        if (d.Rebased is { } silencedBy)
            w.WriteString("silenced_by", silencedBy);
        w.WriteEndObject();

        w.WriteStartArray("addresses");
        foreach (var a in d.Addresses.Where(a => a.Responses.Count > 0))
        {
            w.WriteStartObject();
            w.WriteString("address", Hex(a.Address));
            w.WriteNumber("base", a.BaseValue);
            w.WriteBoolean("trigger", a.IsTrigger);
            w.WriteNumber("responsive", a.Responses.Count);
            w.WriteNumber("consumed", a.Consumed);
            w.WriteNumber("restored", a.Restored);
            w.WriteNumber("distinct", a.Distinct);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("sounds");
        foreach (var s in sounds)
        {
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteString("queue", Hex(s.Address));
            w.WriteNumber("value", s.Value);
            w.WriteString("kind", s.Kind);
            w.WriteString("end", s.End);
            w.WriteNumber("frames", s.Frames);
            if (s.LoopStart is { } ls) w.WriteNumber("loop_start", ls);
            if (s.LoopFrames is { } lf) w.WriteNumber("loop_frames", lf);
            w.WriteStartArray("channels");
            foreach (var c in s.Channels) w.WriteNumberValue(c);
            w.WriteEndArray();
            w.WriteString("hash", s.Hash.ToString("x16"));
            w.WriteStartArray("aliases");
            foreach (var alias in s.Aliases) w.WriteStringValue(alias);
            w.WriteEndArray();
            w.WriteStartArray("init");
            foreach (var (a, v) in s.Init)
            {
                w.WriteStartArray();
                w.WriteNumberValue(a);
                w.WriteNumberValue(v);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("writes");
            foreach (var (f, a, v) in s.Writes)
            {
                w.WriteStartArray();
                w.WriteNumberValue(f);
                w.WriteNumberValue(a);
                w.WriteNumberValue(v);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static string Hex(ushort value) => $"0x{value:X4}";
}
