using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StockInventory.Quotes;
using StockInventory.Web.Services;

namespace StockInventory.Web.Hubs;

public sealed class ConnectionState(Guid userId)
{
    public Guid UserId { get; } = userId;
    public volatile int[]? PortfolioIds;
    public volatile bool Merge = true;
    public volatile bool Paused;
}

/// <summary>§9:ConcurrentDictionary&lt;connectionId, { UserId, PortfolioIds?, Merge, Paused }&gt;;連線中斷時移除。</summary>
public sealed class ConnectionRegistry(TimeProvider clock) : IActiveConnectionCounter
{
    /// <summary>備援輪詢(API-06)最近一次出現的時間在這個視窗內,也算「有人在看」。</summary>
    public static readonly TimeSpan PollingWindow = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, ConnectionState> _map = new();
    private long _lastPollTicks;

    /// <summary>頁面連不上 SignalR 時會每 5 秒輪詢 API-06;沒有這個登記,抓價服務會以為沒人看而退化成每 5 分鐘抓一次。</summary>
    public void TouchPolling() => Interlocked.Exchange(ref _lastPollTicks, clock.GetUtcNow().UtcTicks);
    public void Add(string id, Guid userId) => _map[id] = new ConnectionState(userId);
    public void Remove(string id) => _map.TryRemove(id, out _);
    public ConnectionState? Get(string id) => _map.GetValueOrDefault(id);
    public IReadOnlyList<KeyValuePair<string, ConnectionState>> All() => _map.ToList();
    public int ActiveCount =>
        _map.Values.Count(c => !c.Paused) // 已 Pause 的不計入「有連線」
        + (clock.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastPollTicks) <= PollingWindow.Ticks && Interlocked.Read(ref _lastPollTicks) != 0 ? 1 : 0);
}

[Authorize]
public sealed class ViewHub(ConnectionRegistry registry, ViewService views) : Hub
{
    private Guid UserId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!); // 只取自登入身分

    public override Task OnConnectedAsync()
    {
        registry.Add(Context.ConnectionId, UserId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>記錄此連線正在看的組合(null = 全部),並立即推一次 ViewUpdated。</summary>
    public async Task SetView(int[]? portfolioIds, bool merge)
    {
        var st = registry.Get(Context.ConnectionId);
        if (st is null) return;
        st.PortfolioIds = portfolioIds;
        st.Merge = merge;
        await PushAsync(st);
    }

    public Task Pause()
    {
        if (registry.Get(Context.ConnectionId) is { } st) st.Paused = true;
        return Task.CompletedTask;
    }

    public async Task Resume()
    {
        if (registry.Get(Context.ConnectionId) is not { } st) return;
        st.Paused = false;
        await PushAsync(st);
    }

    private async Task PushAsync(ConnectionState st) =>
        await Clients.Caller.SendAsync("ViewUpdated", await views.BuildAsync(st.UserId, st.PortfolioIds, st.Merge, Context.ConnectionAborted));
}

/// <summary>報價套用後對每條未暫停的連線,以其 UserId、組合與 merge 重新計算並推播;狀態改變時推送 MarketStatus。</summary>
public sealed class ViewBroadcaster(ConnectionRegistry registry, IHubContext<ViewHub> hub, IServiceScopeFactory scopes,
    ILogger<ViewBroadcaster> log) : IQuoteSubscriber
{
    public async Task OnQuotesAppliedAsync(CancellationToken ct)
    {
        foreach (var (id, st) in registry.All())
        {
            if (st.Paused) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var view = await scope.ServiceProvider.GetRequiredService<ViewService>()
                    .BuildAsync(st.UserId, st.PortfolioIds, st.Merge, ct);
                await hub.Clients.Client(id).SendAsync("ViewUpdated", view, ct); // 只送給該連線,不會送給其他使用者
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("推播失敗 ({Type})", ex.GetType().Name);
            }
        }
    }

    public async Task OnTickAsync(MarketStatusSnapshot s, bool statusChanged, CancellationToken ct)
    {
        if (!statusChanged) return;
        await hub.Clients.All.SendAsync("MarketStatus", new MarketDto(s.State.ToString().ToLowerInvariant(), s.Stale,
            s.LastFetchedAtUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null), ct);
    }
}
