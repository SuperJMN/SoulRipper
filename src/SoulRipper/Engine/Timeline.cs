using SoulRipper.Machines;

namespace SoulRipper;

// Sound-chip writes grouped by frame, in bus order.
internal sealed class Timeline
{
    public List<List<(ushort Address, byte Value)>> Frames { get; } = new();

    public int Count => Frames.Count;

    public ulong FrameHash(int frame)
    {
        var h = 1469598103934665603UL;
        foreach (var (a, v) in Frames[frame])
            h = (h ^ ((ulong)a << 8 | v)) * 1099511628211UL;
        return h;
    }

    // Writes of `frame` that the reference did not make in its same frame
    // (multiset difference, order preserved). Concurrent music cancels out.
    public List<(ushort Address, byte Value)> DiffFrame(int frame, Timeline reference)
    {
        var mine = Frames[frame];
        if (frame >= reference.Count)
            return mine.ToList();

        var pool = new Dictionary<(ushort, byte), int>();
        foreach (var w in reference.Frames[frame])
            pool[w] = pool.GetValueOrDefault(w) + 1;

        var result = new List<(ushort, byte)>();
        foreach (var w in mine)
        {
            if (pool.TryGetValue(w, out var n) && n > 0) pool[w] = n - 1;
            else result.Add(w);
        }
        return result;
    }
}

// Records sound-chip writes of a machine frame by frame.
internal static class Recorder
{
    public static Timeline Record(IMachine m, int frames, Func<int, bool>? afterFrame = null) =>
        Extend(m, new Timeline(), frames, afterFrame);

    // Runs up to `frames` more frames, appending to `timeline`; `afterFrame`
    // returns false to stop early.
    public static Timeline Extend(IMachine m, Timeline timeline, int frames, Func<int, bool>? afterFrame = null)
    {
        List<(ushort, byte)>? current = null;
        var previous = m.Write;
        m.Write = (a, v, pc) =>
        {
            previous?.Invoke(a, v, pc);
            if (m.Chip.Owns(a)) current!.Add((a, v));
        };
        try
        {
            for (var i = 0; i < frames; i++)
            {
                current = new List<(ushort, byte)>();
                timeline.Frames.Add(current);
                m.RunFrames(1);
                if (afterFrame != null && !afterFrame(timeline.Count - 1))
                    break;
            }
        }
        finally
        {
            m.Write = previous;
        }
        return timeline;
    }
}
