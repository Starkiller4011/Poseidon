using Poseidon.Modules.Charts.S101;

namespace Poseidon.Modules.Charts.Store;

// Decoded cells in memory (C32, C33), keyed by content so every snapshot sharing a cell's chain shares
// its decoded dataset. The current neighbourhood is pinned; up to capacity other cells stay warm, least
// recently used first out. Evicting never loses data: a later request decodes the cell from the store.
internal sealed class DatasetCache(int capacity)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> recency = new();
    private HashSet<string> pinned = new(StringComparer.Ordinal);
    private int loads;
    private int evictions;

    public CacheStatistics Statistics
    {
        get
        {
            lock (gate)
                return new CacheStatistics(entries.Values.Select(e => e.Name).Distinct().ToList(), loads, evictions);
        }
    }

    public S101Dataset? Get(string key, string name, Func<S101Dataset?> load)
    {
        lock (gate)
        {
            var dataset = GetLocked(key, name, load);
            Trim();
            return dataset;
        }
    }

    public void Put(string key, string name, S101Dataset dataset)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
                Remove(existing);
            Add(key, name, dataset);
            Trim();
        }
    }

    // Makes these cells the pinned neighbourhood, loading any that are cold.
    public void Pin(IReadOnlyList<(string Key, string Name, Func<S101Dataset?> Load)> cells)
    {
        lock (gate)
        {
            pinned = cells.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, name, load) in cells)
                GetLocked(key, name, load);
            Trim();
        }
    }

    private S101Dataset? GetLocked(string key, string name, Func<S101Dataset?> load)
    {
        if (entries.TryGetValue(key, out var entry))
        {
            recency.Remove(entry.Node);
            recency.AddFirst(entry.Node);
            return entry.Dataset;
        }
        loads++;
        var dataset = load();
        if (dataset is not null)
            Add(key, name, dataset);
        return dataset;
    }

    private void Add(string key, string name, S101Dataset dataset)
        => entries[key] = new Entry(name, dataset, recency.AddFirst(key));

    private void Remove(Entry entry)
    {
        entries.Remove(entry.Node.Value);
        recency.Remove(entry.Node);
    }

    private void Trim()
    {
        var unpinned = entries.Keys.Count(k => !pinned.Contains(k));
        for (var node = recency.Last; node is not null && unpinned > capacity;)
        {
            var previous = node.Previous;
            if (!pinned.Contains(node.Value))
            {
                Remove(entries[node.Value]);
                evictions++;
                unpinned--;
            }
            node = previous;
        }
    }

    private sealed record Entry(string Name, S101Dataset Dataset, LinkedListNode<string> Node);
}
