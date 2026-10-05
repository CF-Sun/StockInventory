using System.Collections.Concurrent;
using StockInventory.Core;

namespace StockInventory.Quotes;

/// <summary>記憶體報價快取(§7.3):ConcurrentDictionary&lt;Symbol, QuoteInput&gt;。</summary>
public sealed class QuoteCacheStore : IQuoteCache
{
    private readonly ConcurrentDictionary<string, QuoteInput> _map = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, QuoteInput> Snapshot() => new Dictionary<string, QuoteInput>(_map);
    public void Set(string symbol, QuoteInput q) => _map[symbol] = q;
    public void Load(IEnumerable<KeyValuePair<string, QuoteInput>> items) { foreach (var kv in items) _map[kv.Key] = kv.Value; }
}
