namespace DotWasm.Runtime.Component;

/// <summary>A live resource handle held in a component instance's handle table.</summary>
public sealed class ResourceHandle(ResourceTypeIdentity type, int rep, bool own)
{
    public ResourceTypeIdentity Type { get; } = type;
    public int Rep { get; } = rep;
    public bool Own { get; } = own;

    /// <summary>Number of outstanding borrows lent from this (owned) handle.</summary>
    public int NumLends { get; set; }
}

/// <summary>
/// Per-instance handle table (the spec's <c>Table</c>). Index 0 is reserved as a sentinel,
/// holes are reused via a free list. Used for resource <c>own</c>/<c>borrow</c> handles.
/// </summary>
public sealed class ComponentInstanceHandles
{
    const int MaxLength = (1 << 28) - 1;

    readonly List<ResourceHandle?> array = [null]; // index 0 reserved
    readonly Stack<int> free = new();

    public ResourceHandle Get(int i)
    {
        if (i <= 0 || i >= array.Count || array[i] is null)
            throw new WasmTrapException($"Invalid resource handle index {i}.");
        return array[i]!;
    }

    public int Add(ResourceHandle h)
    {
        if (free.Count > 0)
        {
            var i = free.Pop();
            array[i] = h;
            return i;
        }
        var idx = array.Count;
        if (idx > MaxLength)
            throw new WasmTrapException("Resource handle table overflow.");
        array.Add(h);
        return idx;
    }

    public ResourceHandle Remove(int i)
    {
        var h = Get(i);
        array[i] = null;
        free.Push(i);
        return h;
    }
}
