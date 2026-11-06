namespace Kid.Mcp;

internal sealed class CompactReferenceStore<T>(int capacity, TimeSpan lifetime, TimeProvider? clock = null)
{
    private readonly Dictionary<string, Entry> entries = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();

    internal string Add(int windowHandle, T value)
    {
        lock (gate)
        {
            RemoveExpired();
            while (entries.Count >= capacity)
            {
                entries.Remove(entries.MinBy(pair => pair.Value.Expires).Key);
            }

            var id = Guid.NewGuid().ToString("N")[..12];
            entries.Add(id, new Entry(windowHandle, value, time.GetUtcNow() + lifetime));
            return id;
        }
    }

    internal T Get(string id)
    {
        lock (gate)
        {
            RemoveExpired();
            if (!entries.TryGetValue(id, out var entry))
            {
                throw new ModelContextProtocol.McpException("Element reference expired or is unknown. Read the window again.");
            }

            return entry.Value;
        }
    }

    internal void InvalidateWindow(int windowHandle)
    {
        lock (gate)
        {
            foreach (var id in entries.Where(pair => pair.Value.WindowHandle == windowHandle).Select(pair => pair.Key).ToArray())
            {
                entries.Remove(id);
            }
        }
    }

    private void RemoveExpired()
    {
        var now = time.GetUtcNow();
        foreach (var id in entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
        {
            entries.Remove(id);
        }
    }

    private sealed record Entry(int WindowHandle, T Value, DateTimeOffset Expires);
}
