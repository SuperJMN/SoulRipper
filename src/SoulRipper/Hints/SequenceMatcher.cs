namespace SoulRipper.Hints;

// Ratcliff/Obershelp similarity, as Python's difflib.SequenceMatcher computes
// it (without junk heuristics): twice the matched elements over the total.
internal static class SequenceMatcher
{
    public static double Ratio<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : notnull
    {
        if (a.Count + b.Count == 0)
            return 1.0;

        var b2j = new Dictionary<T, List<int>>();
        for (var j = 0; j < b.Count; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list)) b2j[b[j]] = list = new List<int>();
            list.Add(j);
        }

        var matched = 0;
        var queue = new Stack<(int ALo, int AHi, int BLo, int BHi)>();
        queue.Push((0, a.Count, 0, b.Count));
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = LongestMatch(a, b2j, alo, ahi, blo, bhi);
            if (k == 0) continue;
            matched += k;
            if (alo < i && blo < j) queue.Push((alo, i, blo, j));
            if (i + k < ahi && j + k < bhi) queue.Push((i + k, ahi, j + k, bhi));
        }
        return 2.0 * matched / (a.Count + b.Count);
    }

    // Earliest longest block a[i..i+k) == b[j..j+k) within the ranges.
    private static (int I, int J, int K) LongestMatch<T>(
        IReadOnlyList<T> a, Dictionary<T, List<int>> b2j, int alo, int ahi, int blo, int bhi) where T : notnull
    {
        var (besti, bestj, bestk) = (alo, blo, 0);
        var lengths = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var next = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var js))
                foreach (var j in js)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    var k = next[j] = lengths.GetValueOrDefault(j - 1) + 1;
                    if (k > bestk) (besti, bestj, bestk) = (i - k + 1, j - k + 1, k);
                }
            lengths = next;
        }
        return (besti, bestj, bestk);
    }
}
