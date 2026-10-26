using System.Reflection;

namespace SoulRipper.Machines;

// Exact in-memory savestate for an object-graph emulator (CoreBoy): records
// every instance field of every object reachable from the root and writes them
// back in place. Objects created after the snapshot simply become unreachable
// again, so the restored machine is bit-identical to the captured one.
internal sealed class ObjectGraphSnapshot
{
    private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new();

    private readonly List<(object Target, FieldInfo[] Fields, object?[] Values)> objects = new();
    private readonly List<(Array Target, Array Copy)> arrays = new();

    private ObjectGraphSnapshot()
    {
    }

    // `skip` objects (display, controller, ...) are neither recorded nor walked.
    public static ObjectGraphSnapshot Capture(object root, Func<object, bool> skip)
    {
        var snapshot = new ObjectGraphSnapshot();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current) || skip(current))
                continue;

            if (current is Array array)
            {
                snapshot.arrays.Add((array, (Array)array.Clone()));
                if (!array.GetType().GetElementType()!.IsValueType)
                    foreach (var item in array)
                        if (item != null && IsWalkable(item.GetType()))
                            pending.Push(item);
                continue;
            }

            var fields = FieldsOf(current.GetType());
            var values = new object?[fields.Length];
            for (var i = 0; i < fields.Length; i++)
            {
                var value = fields[i].GetValue(current);
                values[i] = value;
                if (value != null && IsWalkable(value.GetType()))
                    pending.Push(value);
            }
            snapshot.objects.Add((current, fields, values));
        }

        return snapshot;
    }

    public void Restore()
    {
        foreach (var (target, copy) in arrays)
            Array.Copy(copy, target, copy.Length);
        foreach (var (target, fields, values) in objects)
            for (var i = 0; i < fields.Length; i++)
                fields[i].SetValue(target, values[i]);
    }

    private static bool IsWalkable(Type type) =>
        !type.IsValueType && type != typeof(string) && !typeof(Delegate).IsAssignableFrom(type)
        && !typeof(MemberInfo).IsAssignableFrom(type);

    // Delegate fields (bus observers, callbacks) are wiring, not state.
    private static FieldInfo[] FieldsOf(Type type)
    {
        if (FieldCache.TryGetValue(type, out var cached))
            return cached;

        var fields = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !typeof(Delegate).IsAssignableFrom(f.FieldType)));

        return FieldCache[type] = fields.ToArray();
    }
}
