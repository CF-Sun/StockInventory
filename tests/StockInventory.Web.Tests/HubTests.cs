using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using StockInventory.Web.Services;
using Xunit;
using A = StockInventory.Web.Tests.AdminApiTests;

namespace StockInventory.Web.Tests;

/// <summary>測試用:在 TestServer 的 handler 外包一層簡單的 Cookie 處理,讓 HttpClient 與 SignalR 共用登入 Cookie。</summary>
sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    readonly ConcurrentDictionary<string, string> _cookies = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (!_cookies.IsEmpty) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        var res = await base.SendAsync(req, ct);
        if (res.Headers.TryGetValues("Set-Cookie", out var sets))
            foreach (var sc in sets)
            {
                var nv = sc.Split(';')[0];
                var i = nv.IndexOf('=');
                if (nv[(i + 1)..].Length == 0) _cookies.TryRemove(nv[..i], out _); else _cookies[nv[..i]] = nv[(i + 1)..];
            }
        return res;
    }
}

public class HubTests
{
    static (HttpClient Client, CookieHandler Handler) NewSession(TestFactory f)
    {
        var h = new CookieHandler(f.Server.CreateHandler());
        return (new HttpClient(h) { BaseAddress = new Uri("https://localhost") }, h);
    }

    static HubConnection Connect(CookieHandler h) =>
        new HubConnectionBuilder().WithUrl(new Uri("https://localhost/hubs/view"), o =>
        {
            o.HttpMessageHandlerFactory = _ => h;
            o.Transports = HttpTransportType.LongPolling;
        }).Build();

    static async Task<(TestFactory f, HubConnection alice, HubConnection bob, BlockingQueue<ViewDto> aq, BlockingQueue<ViewDto> bq)> Setup()
    {
        var f = new TestFactory();
        using (var s = f.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Instruments.Add(new Instrument { Symbol = "0050", Name = "元大台灣50", Market = Market.Twse, Kind = InstrumentKind.Etf, UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var (ac, ah) = NewSession(f); var (bc, bh) = NewSession(f);
        var (_, ca, _) = await A.SignInAsync(f, "alice", ac, "User");
        var (_, cb, _) = await A.SignInAsync(f, "bob", bc, "User");
        // alice:1000 股 0050;bob:2000 股 0050
        foreach (var (c, csrf, shares) in new[] { (ac, ca, 1000), (bc, cb, 2000) })
        {
            var pid = (await A.Json(await c.SendAsync(A.Req(HttpMethod.Post, "/api/portfolios", csrf, new { name = "P" })))).GetProperty("id").GetInt32();
            await c.SendAsync(A.Req(HttpMethod.Post, $"/api/portfolios/{pid}/holdings", csrf, new { symbol = "0050", totalCost = 100000, shares }));
        }
        var alice = Connect(ah); var bob = Connect(bh);
        var aq = new BlockingQueue<ViewDto>(); var bq = new BlockingQueue<ViewDto>();
        alice.On<ViewDto>("ViewUpdated", v => aq.Add(v));
        bob.On<ViewDto>("ViewUpdated", v => bq.Add(v));
        await alice.StartAsync(); await bob.StartAsync();
        return (f, alice, bob, aq, bq);
    }

    sealed class BlockingQueue<T>
    {
        readonly System.Threading.Channels.Channel<T> _ch = System.Threading.Channels.Channel.CreateUnbounded<T>();
        public void Add(T x) => _ch.Writer.TryWrite(x);
        public async Task<T> TakeAsync(int ms = 5000)
        {
            using var cts = new CancellationTokenSource(ms);
            return await _ch.Reader.ReadAsync(cts.Token);
        }
        public async Task<bool> NothingAsync(int ms = 400)
        {
            using var cts = new CancellationTokenSource(ms);
            try { await _ch.Reader.ReadAsync(cts.Token); return false; } catch (OperationCanceledException) { return true; }
        }
    }

    [Fact]
    public async Task Unauthenticated_Cannot_Connect()
    {
        using var f = new TestFactory();
        var c = new HubConnectionBuilder().WithUrl(new Uri("https://localhost/hubs/view"), o =>
        {
            o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
        }).Build();
        await Assert.ThrowsAnyAsync<Exception>(() => c.StartAsync());
    }

    [Fact]
    public async Task SetView_PushesOwnView_Pause_Resume_AndBroadcastIsolation()
    {
        var (f, alice, bob, aq, bq) = await Setup();
        using var _ = f;
        var registry = f.Services.GetRequiredService<Web.Hubs.ConnectionRegistry>();
        Assert.Equal(2, registry.ActiveCount);

        await alice.InvokeAsync("SetView", (int[]?)null, true);
        var av = await aq.TakeAsync();
        Assert.Equal(1000, av.Rows[0].Shares);

        await bob.InvokeAsync("SetView", (int[]?)null, true);
        Assert.Equal(2000, (await bq.TakeAsync()).Rows[0].Shares);   // 各自看到自己的資料

        // 報價套用後:對每條未暫停的連線推播,且各看各的
        var broadcaster = f.Services.GetServices<IQuoteSubscriber>().OfType<Web.Hubs.ViewBroadcaster>().Single();
        await broadcaster.OnQuotesAppliedAsync(default);
        Assert.Equal(1000, (await aq.TakeAsync()).Rows[0].Shares);
        Assert.Equal(2000, (await bq.TakeAsync()).Rows[0].Shares);

        // Pause:不推播,且不計入「有連線」
        await alice.InvokeAsync("Pause");
        Assert.Equal(1, registry.ActiveCount);
        await broadcaster.OnQuotesAppliedAsync(default);
        Assert.True(await aq.NothingAsync());
        Assert.Equal(2000, (await bq.TakeAsync()).Rows[0].Shares);

        // Resume:立即補一次
        await alice.InvokeAsync("Resume");
        Assert.Equal(1000, (await aq.TakeAsync()).Rows[0].Shares);
        Assert.Equal(2, registry.ActiveCount);

        // 斷線:移除
        await bob.StopAsync();
        await Task.Delay(300);
        Assert.Equal(1, registry.ActiveCount);
        await alice.StopAsync();
    }

    [Fact]
    public async Task MarketStatus_PushedOnChange_AndOnlyOnChange()
    {
        var (f, alice, _, _, _) = await Setup();
        using var __ = f;
        var q = new BlockingQueue<MarketDto>();
        alice.On<MarketDto>("MarketStatus", m => q.Add(m));
        var b = f.Services.GetServices<IQuoteSubscriber>().OfType<Web.Hubs.ViewBroadcaster>().Single();

        await b.OnTickAsync(new MarketStatusSnapshot(MarketState.Open, true, DateTime.UtcNow), true, default);
        var m = await q.TakeAsync();
        Assert.Equal(("open", true), (m.State, m.Stale));
        await b.OnTickAsync(new MarketStatusSnapshot(MarketState.Open, true, DateTime.UtcNow), false, default);
        Assert.True(await q.NothingAsync());
    }
}
