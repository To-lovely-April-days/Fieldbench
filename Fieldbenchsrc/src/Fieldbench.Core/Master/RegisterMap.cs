using Fieldbench.Core.Modbus;

namespace Fieldbench.Core.Master;

/// <summary>
/// The tag table of a master session plus the raw word/bit cache filled by poll
/// results. Tags overlapping a polled range recompute automatically.
/// </summary>
public sealed class RegisterMap
{
    private readonly object _gate = new();
    private readonly Dictionary<RegisterArea, Dictionary<int, ushort>> _words = new();
    private readonly Dictionary<RegisterArea, Dictionary<int, bool>> _bits = new();
    private readonly List<RegisterTag> _tags = new();

    public event Action? TagsChanged;

    public event Action<RegisterTag>? TagUpdated;

    public IReadOnlyList<RegisterTag> Tags
    {
        get { lock (_gate) return _tags.ToArray(); }
    }

    public void AddTag(RegisterTag tag)
    {
        lock (_gate)
        {
            _tags.Add(tag);
            _tags.Sort((a, b) => a.Area != b.Area ? a.Area.CompareTo(b.Area) : a.Address.CompareTo(b.Address));
        }

        tag.Updated += t => TagUpdated?.Invoke(t);
        TagsChanged?.Invoke();
    }

    public void RemoveTag(RegisterTag tag)
    {
        lock (_gate) _tags.Remove(tag);
        TagsChanged?.Invoke();
    }

    public void NotifyTagsEdited() => TagsChanged?.Invoke();

    /// <summary>Apply a successful register read (FC03/04) to the cache and affected tags.</summary>
    public void ApplyWordRead(RegisterArea area, ushort start, ReadOnlySpan<byte> data, DateTime tsUtc, long? frameId = null)
    {
        int count = data.Length / 2;
        lock (_gate)
        {
            var cache = CacheFor(area);
            for (int i = 0; i < count; i++)
            {
                cache[start + i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
            }
        }

        RefreshTags(area, start, count, tsUtc, frameId);
    }

    /// <summary>Apply a successful bit read (FC01/02).</summary>
    public void ApplyBitRead(RegisterArea area, ushort start, int quantity, ReadOnlySpan<byte> data, DateTime tsUtc, long? frameId = null)
    {
        // A short (malformed) response must not crash the poll loop.
        quantity = Math.Min(quantity, data.Length * 8);
        lock (_gate)
        {
            var cache = BitCacheFor(area);
            for (int i = 0; i < quantity; i++)
            {
                cache[start + i] = (data[i / 8] & (1 << (i % 8))) != 0;
            }
        }

        RefreshTags(area, start, quantity, tsUtc, frameId);
    }

    private void RefreshTags(RegisterArea area, int start, int count, DateTime tsUtc, long? frameId)
    {
        RegisterTag[] affected;
        lock (_gate)
        {
            affected = _tags.Where(t => t.Area == area && t.Address < start + count && t.Address + t.RegisterCount > start).ToArray();
        }

        foreach (var tag in affected)
        {
            if (area.IsBitArea())
            {
                bool? v;
                lock (_gate)
                {
                    v = BitCacheFor(area).TryGetValue(tag.Address, out var b) ? b : null;
                }

                if (v is { } bit) tag.UpdateFromBit(bit, tsUtc, frameId);
            }
            else
            {
                var words = new ushort[tag.RegisterCount];
                bool complete = true;
                lock (_gate)
                {
                    var cache = CacheFor(area);
                    for (int i = 0; i < words.Length; i++)
                    {
                        if (!cache.TryGetValue(tag.Address + i, out words[i]))
                        {
                            complete = false;
                            break;
                        }
                    }
                }

                if (complete) tag.UpdateFromWords(words, tsUtc, frameId);
            }
        }
    }

    private Dictionary<int, ushort> CacheFor(RegisterArea area)
    {
        if (!_words.TryGetValue(area, out var cache)) _words[area] = cache = new Dictionary<int, ushort>();
        return cache;
    }

    private Dictionary<int, bool> BitCacheFor(RegisterArea area)
    {
        if (!_bits.TryGetValue(area, out var cache)) _bits[area] = cache = new Dictionary<int, bool>();
        return cache;
    }
}
