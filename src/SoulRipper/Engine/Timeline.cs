using SoulRipper.Machines;

namespace SoulRipper;

// Sound-chip writes grouped by frame, in bus order.
internal sealed class Timeline
{
    private const int TailFrames = 60;   // about a second

    public List<List<(ushort Address, byte Value)>> Frames { get; } = new();

    public int Count => Frames.Count;

    private (int Count, List<List<(ushort, byte)>> Frames)? tail;

    public ulong FrameHash(int frame)
    {
        var h = 1469598103934665603UL;
        foreach (var (a, v) in Frames[frame])
            h = (h ^ ((ulong)a << 8 | v)) * 1099511628211UL;
        return h;
    }

    // Writes of `frame` that the reference did not make in its same frame
    // (multiset difference, order preserved). Concurrent music cancels out.
    // Past its end the reference is taken to keep writing as in its last
    // second, which is what a silenced driver keeps doing: otherwise a sound
    // that outlasts it (SMB2's title music) never ends and loops on the idle
    // writes. Any frame of that second will do: SML's idle driver writes twice
    // in one frame of 14, and against the last frame alone those second
    // writes made Can-Can loop on silence.
    public List<(ushort Address, byte Value)> DiffFrame(int frame, Timeline reference)
    {
        var mine = Frames[frame];
        if (reference.Count == 0)
            return mine.ToList();
        if (frame < reference.Count)
            return Except(mine, reference.Frames[frame]);

        List<(ushort, byte)>? best = null;
        foreach (var idle in reference.Tail())
        {
            var diff = Except(mine, idle);
            if (best == null || diff.Count < best.Count) best = diff;
            if (best.Count == 0) break;
        }
        return best!;
    }

    private static List<(ushort, byte)> Except(List<(ushort Address, byte Value)> mine, List<(ushort Address, byte Value)> other)
    {
        var pool = new Dictionary<(ushort, byte), int>();
        foreach (var w in other)
            pool[w] = pool.GetValueOrDefault(w) + 1;

        var result = new List<(ushort, byte)>();
        foreach (var w in mine)
        {
            if (pool.TryGetValue(w, out var n) && n > 0) pool[w] = n - 1;
            else result.Add(w);
        }
        return result;
    }

    // The distinct frames of the timeline's last second.
    private List<List<(ushort, byte)>> Tail()
    {
        if (tail is { } cached && cached.Count == Count)
            return cached.Frames;

        var start = Math.Max(0, Count - TailFrames);
        var frames = Enumerable.Range(start, Count - start).DistinctBy(FrameHash).Select(f => Frames[f]).ToList();
        tail = (Count, frames);
        return frames;
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
