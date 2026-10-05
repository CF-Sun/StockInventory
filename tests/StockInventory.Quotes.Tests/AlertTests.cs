using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StockInventory.Core;
using StockInventory.Quotes;
using Xunit;

namespace StockInventory.Quotes.Tests;

public class AlertTests
{
    sealed class Rec : IAlertSender
    {
        public List<string> Subjects = [];
        public Task SendAsync(string subject, string body, CancellationToken ct = default) { Subjects.Add(subject); return Task.CompletedTask; }
    }

    static MarketStatusSnapshot S(bool stale, MarketState st = MarketState.Open) => new(st, stale, DateTime.UtcNow);

    [Fact]
    public async Task Stale_SendsOnce_Cooldown30Min_ThenRecoveryOnce()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var rec = new Rec();
        var sub = new StaleAlertSubscriber(rec, Options.Create(new AlertOptions()), clock);

        await sub.OnTickAsync(S(false), false, default);
        Assert.Empty(rec.Subjects);

        await sub.OnTickAsync(S(true), true, default);
        Assert.Single(rec.Subjects);
        clock.Advance(TimeSpan.FromMinutes(29));
        await sub.OnTickAsync(S(true), false, default);
        Assert.Single(rec.Subjects);                       // 冷卻中
        clock.Advance(TimeSpan.FromMinutes(1));
        await sub.OnTickAsync(S(true), false, default);
        Assert.Equal(2, rec.Subjects.Count);               // 滿 30 分鐘再寄一封

        await sub.OnTickAsync(S(false), true, default);
        Assert.Equal(3, rec.Subjects.Count);
        Assert.Contains("恢復", rec.Subjects[2]);
        await sub.OnTickAsync(S(false), false, default);
        Assert.Equal(3, rec.Subjects.Count);               // 恢復通知只寄一次
    }

    [Fact]
    public async Task Stale_WhileClosed_DoesNotAlert()
    {
        var rec = new Rec();
        var sub = new StaleAlertSubscriber(rec, Options.Create(new AlertOptions()), new FakeTimeProvider());
        await sub.OnTickAsync(S(true, MarketState.Closed), false, default);
        Assert.Empty(rec.Subjects);
    }

    [Fact]
    public async Task LoginFailures_ThresholdPerUserOrIp_WithCooldown_AndWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var rec = new Rec();
        var a = new LoginFailureAlerts(rec, Options.Create(new AlertOptions()), clock); // 門檻 10、視窗 10 分、冷卻 30 分

        for (var i = 0; i < 9; i++) await a.RecordFailureAsync("Alice", $"1.1.1.{i}");
        Assert.Empty(rec.Subjects);                                  // 帳號 9 次、各 IP 1 次
        await a.RecordFailureAsync("alice", "9.9.9.9");              // 帳號(不分大小寫)第 10 次
        Assert.Single(rec.Subjects);
        await a.RecordFailureAsync("alice", "9.9.9.9");
        Assert.Single(rec.Subjects);                                 // 冷卻中

        for (var i = 0; i < 10; i++) await a.RecordFailureAsync($"user{i}", "5.5.5.5"); // 同一 IP 10 次
        Assert.Equal(2, rec.Subjects.Count);

        var slow = new LoginFailureAlerts(rec, Options.Create(new AlertOptions()), clock);
        for (var i = 0; i < 20; i++) { await slow.RecordFailureAsync("bob", "7.7.7.7"); clock.Advance(TimeSpan.FromMinutes(2)); } // 視窗內最多 5 次
        Assert.Equal(2, rec.Subjects.Count);
    }
}
