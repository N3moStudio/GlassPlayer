namespace GlassPlayer;

// Small, expiring metadata caches. No audio buffers or account credentials.
sealed class RecentCache<TKey, TValue>(int capacity, TimeSpan lifetime) where TKey : notnull
{
    readonly Dictionary<TKey, (TValue Value, DateTime Expires)> entries = new();
    public bool TryGet(TKey key, out TValue value)
    {
        lock (entries)
        {
            if (entries.TryGetValue(key, out var entry) && entry.Expires > DateTime.UtcNow)
            { value = entry.Value; return true; }
            entries.Remove(key); value = default!; return false;
        }
    }
    public void Set(TKey key, TValue value)
    {
        lock (entries)
        {
            entries.Remove(key);
            while (entries.Count >= capacity) entries.Remove(entries.First().Key);
            entries[key] = (value, DateTime.UtcNow + lifetime);
        }
    }
    public void Remove(TKey key) { lock (entries) entries.Remove(key); }
    public void Clear() { lock (entries) entries.Clear(); }
    internal int Count { get { lock (entries) return entries.Count; } }
}
