namespace StockInventory.Quotes;

/// <summary>所有批次共用的滑動視窗速率限制:每個視窗最多 max 次,超過就等到最舊的一次滑出視窗(§7.3)。</summary>
public sealed class SlidingWindowLimiter(int max, TimeSpan window, TimeProvider clock, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Queue<DateTimeOffset> _stamps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((t, ct) => Task.Delay(t, clock, ct));

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = clock.GetUtcNow();
                while (_stamps.Count > 0 && now - _stamps.Peek() >= window) _stamps.Dequeue();
                if (_stamps.Count < max) { _stamps.Enqueue(now); return; }
                await _delay(_stamps.Peek() + window - now, ct);
            }
        }
        finally { _gate.Release(); }
    }
}
