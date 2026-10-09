using StockInventory.Core;
using Xunit;

namespace StockInventory.Core.Tests;

/// <summary>FR-26 走勢純函式:UT-12 至 UT-15,以及 UT-09 修改部分(market.stale 門檻)。</summary>
public class IntradayTests
{
    static readonly TimeSpan Start = new(9, 0, 0), End = new(13, 30, 0);
    static readonly DateTime Day = new(2026, 10, 5); // 週一

    static bool Rec(int h, int m, int s, PriceSource src = PriceSource.Trade, MarketState st = MarketState.Open,
        DateTime? quote = null, bool quoteNull = false)
    {
        var fetched = Day.AddHours(h).AddMinutes(m).AddSeconds(s);
        DateTime? q = quoteNull ? null : quote ?? fetched.AddSeconds(-20);
        return IntradayRules.ShouldRecord(src, st, fetched, q, Start, End);
    }

    // ---------- UT-12 ----------
    [Theory]
    [InlineData(8, 59, 59, false)]
    [InlineData(9, 0, 0, true)]
    [InlineData(13, 30, 0, true)]
    [InlineData(13, 30, 59, true)]
    [InlineData(13, 31, 0, false)]
    public void ShouldRecord_TimeWindowBoundaries(int h, int m, int s, bool expected) => Assert.Equal(expected, Rec(h, m, s));

    [Theory]
    [InlineData(PriceSource.MidQuote)]
    [InlineData(PriceSource.PrevClose)]
    public void ShouldRecord_OnlyTradePrice(PriceSource src) => Assert.False(Rec(10, 0, 0, src));

    [Theory]
    [InlineData(MarketState.Closed)]
    [InlineData(MarketState.Holiday)]
    public void ShouldRecord_OnlyWhenOpen(MarketState st) => Assert.False(Rec(10, 0, 0, st: st));

    [Fact]
    public void ShouldRecord_QuoteDateMustBeToday_NullNotRecorded()
    {
        Assert.False(Rec(9, 0, 5, quote: Day.AddDays(-1).AddHours(13).AddMinutes(30))); // 開盤初期 MIS 殘留前一日價格
        Assert.False(Rec(10, 0, 0, quoteNull: true));
        Assert.True(Rec(10, 0, 0, quote: Day.AddHours(9)));                              // 今日較早的成交時間(冷門股)
    }

    [Fact]
    public void FloorToMinute_ZeroesSeconds_KeepsKind()
    {
        var t = new DateTime(2026, 10, 5, 2, 0, 59, 900, DateTimeKind.Utc);
        var f = IntradayRules.FloorToMinute(t);
        Assert.Equal(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc), f);
        Assert.Equal(DateTimeKind.Utc, f.Kind);
    }

    // ---------- UT-13 ----------
    static readonly DateTime T0 = new(2026, 10, 5, 1, 0, 0, DateTimeKind.Utc);

    static List<(DateTime, decimal)> Raw(int n, int stepSeconds = 60) =>
        Enumerable.Range(0, n).Select(i => (T0.AddSeconds(i * stepSeconds), 100m + i)).ToList();

    static IReadOnlyList<IntradayPoint> Marked(int n, int stepSeconds = 60) => IntradayRules.MarkGaps(Raw(n, stepSeconds), 180);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(89, 89)]
    [InlineData(90, 90)]   // 全留
    [InlineData(91, 46)]   // step 2:索引 90, 88, ..., 0
    public void Downsample_Counts(int n, int expected)
    {
        var result = IntradayRules.Downsample(Marked(n), 90);
        Assert.Equal(expected, result.Count);
        if (n > 0) Assert.Equal(T0.AddMinutes(n - 1), result[^1].TimeUtc); // 必含最後一點
    }

    [Fact]
    public void Downsample_91_UsesStep2_FromTheLastPoint()
    {
        var r = IntradayRules.Downsample(Marked(91), 90);
        Assert.Equal(Enumerable.Range(0, 46).Select(k => T0.AddMinutes(k * 2)).ToArray(), r.Select(p => p.TimeUtc).ToArray());
    }

    [Fact]
    public void Downsample_271_Step4_AtMost90_KeepsLast()
    {
        var r = IntradayRules.Downsample(Marked(271), 90);
        Assert.Equal(68, r.Count);
        Assert.True(r.Count <= 90);
        Assert.Equal(T0.AddMinutes(270), r[^1].TimeUtc);
        Assert.Equal(T0.AddMinutes(270 - 4), r[^2].TimeUtc);       // 由最後一點往前每隔 4 點
        Assert.All(r.Zip(r.Skip(1)), x => Assert.Equal(4, (x.Second.TimeUtc - x.First.TimeUtc).TotalMinutes));
    }

    [Fact]
    public void Downsample_WithoutGaps_NoGapFlags() =>
        Assert.All(IntradayRules.Downsample(Marked(271), 90), p => Assert.False(p.Gap));

    [Theory]
    [InlineData(180, false)]  // 剛好 180 秒不算缺口
    [InlineData(181, true)]
    [InlineData(60, false)]
    [InlineData(600, true)]
    public void MarkGaps_Boundary(int seconds, bool expectedGap)
    {
        var raw = new List<(DateTime, decimal)> { (T0, 10m), (T0.AddSeconds(seconds), 11m) };
        var r = IntradayRules.MarkGaps(raw, 180);
        Assert.False(r[0].Gap);              // 第一點為 false
        Assert.Equal(expectedGap, r[1].Gap);
    }

    [Fact]
    public void MarkGaps_Incremental_FirstNewPointComparedWithSince()
    {
        var since = T0;
        var near = IntradayRules.MarkGaps([(T0.AddSeconds(180), 1m)], 180, since);
        var far = IntradayRules.MarkGaps([(T0.AddSeconds(181), 1m)], 180, since);
        Assert.False(near[0].Gap);
        Assert.True(far[0].Gap);
    }

    [Fact]
    public void Downsample_SkippedRangeContainingGap_MarksNextKeptPoint()
    {
        // 91 點,step 2 保留偶數索引(0, 2, ..., 90);原始缺口發生在索引 41(被略過)與 60(被保留)
        var raw = Raw(91);
        var pts = IntradayRules.MarkGaps(raw.Select((r, i) => i < 41 ? r : (r.Item1.AddMinutes(10), r.Item2)).ToList(), 180);
        // 索引 41 與前一點間隔 11 分鐘 → 原始 Gap = true,之後間隔恢復 1 分鐘
        Assert.True(pts[41].Gap);
        Assert.False(pts[42].Gap);
        var r = IntradayRules.Downsample(pts, 90);
        var kept = r.ToDictionary(p => p.Price, p => p.Gap);
        Assert.True(kept[100m + 42]);         // 索引 42:被略過的 41 含缺口 → 下一保留點為 true
        Assert.False(kept[100m + 44]);
        Assert.False(kept[100m + 40]);        // 缺口之前的保留點不受影響
        Assert.False(r[0].Gap);               // 第一個保留點為 false
        Assert.Single(r, p => p.Gap);
    }

    [Fact]
    public void Downsample_GapOnKeptPoint_StaysTrue()
    {
        var raw = Raw(91);
        var pts = IntradayRules.MarkGaps(raw.Select((r, i) => i < 60 ? r : (r.Item1.AddMinutes(10), r.Item2)).ToList(), 180);
        Assert.True(pts[60].Gap);
        var r = IntradayRules.Downsample(pts, 90);
        Assert.True(r.Single(p => p.Price == 160m).Gap);
        Assert.Single(r, p => p.Gap);
    }

    [Fact]
    public void Downsample_InvalidMax_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IntradayRules.Downsample(Marked(3), 0));

    // ---------- UT-14 ----------
    [Fact]
    public void Axis_WithoutPrevClose_UsesHighLowWith5PercentMargin()
    {
        var a = IntradayRules.ComputeAxis([100m, 110m, 105m], null)!;
        Assert.Equal((110m, 100m), (a.High, a.Low));
        Assert.Equal(99.5m, a.YMin);   // 範圍 10 的 5% = 0.5
        Assert.Equal(110.5m, a.YMax);
    }

    [Fact]
    public void Axis_PrevCloseInsideRange_DoesNotChangeBounds()
    {
        var a = IntradayRules.ComputeAxis([100m, 110m], 105m)!;
        Assert.Equal((99.5m, 110.5m), (a.YMin, a.YMax));
    }

    [Fact]
    public void Axis_PrevCloseOutsideRange_ExtendsUnion_ButHighLowStayFromPoints()
    {
        var up = IntradayRules.ComputeAxis([100m, 110m], 120m)!;     // 聯集 100–120,範圍 20 → 邊距 1
        Assert.Equal((110m, 100m), (up.High, up.Low));
        Assert.Equal((99m, 121m), (up.YMin, up.YMax));
        var down = IntradayRules.ComputeAxis([100m, 110m], 90m)!;    // 聯集 90–110
        Assert.Equal((89m, 111m), (down.YMin, down.YMax));
    }

    [Fact]
    public void Axis_AllSamePrice_PlusMinusOnePercent()
    {
        var a = IntradayRules.ComputeAxis([200m, 200m, 200m], null)!;
        Assert.Equal((198m, 202m), (a.YMin, a.YMax));
        var b = IntradayRules.ComputeAxis([200m], 200m)!;             // 與昨收同價也是單一價
        Assert.Equal((198m, 202m), (b.YMin, b.YMax));
    }

    [Fact]
    public void Axis_Empty_ReturnsNull() => Assert.Null(IntradayRules.ComputeAxis([], 100m));

    [Theory]
    [InlineData(101, IntradayTrend.Up)]
    [InlineData(99, IntradayTrend.Down)]
    [InlineData(100, IntradayTrend.Flat)]
    public void Trend_LastPointVsPrevClose(int last, IntradayTrend expected) =>
        Assert.Equal(expected, IntradayRules.GetTrend([150m, 90m, last], 100m));

    [Fact]
    public void Trend_WithoutPrevClose_ComparesWithFirstPoint()
    {
        Assert.Equal(IntradayTrend.Up, IntradayRules.GetTrend([100m, 90m, 101m], null));
        Assert.Equal(IntradayTrend.Down, IntradayRules.GetTrend([100m, 110m, 99m], null));
        Assert.Equal(IntradayTrend.Flat, IntradayRules.GetTrend([100m, 110m, 100m], null));
        Assert.Equal(IntradayTrend.Flat, IntradayRules.GetTrend([100m], null));
        Assert.Null(IntradayRules.GetTrend([], 100m));
    }

    [Fact] // high/low 取降採樣前:降採樣會丟掉的最高點仍須反映在 High
    public void HighLow_AreTakenBeforeDownsampling()
    {
        var raw = Raw(91).Select((r, i) => (r.Item1, i == 41 ? 500m : r.Item2)).ToList(); // 索引 41 會被 step 2 略過
        var down = IntradayRules.Downsample(IntradayRules.MarkGaps(raw, 180), 90);
        Assert.DoesNotContain(down, p => p.Price == 500m);
        Assert.Equal(500m, IntradayRules.ComputeAxis(raw.Select(r => r.Item2).ToList(), null)!.High);
    }

    // ---------- UT-15 ----------
    static readonly DateOnly Mon = new(2026, 10, 5);

    [Theory]
    [InlineData(MarketState.Open)]
    [InlineData(MarketState.Closed)]
    public void DisplayDay_WeekdayOpenOrClosed_IsToday(MarketState st)
    {
        var d = IntradayRules.SelectDisplayDay(st, Mon, new DateOnly(2026, 10, 2), 7);
        Assert.Equal((Mon, true), (d.TradeDate, d.IsToday));
    }

    [Fact]
    public void DisplayDay_Holiday_LatestRecordedWithinRetention()
    {
        var sat = new DateOnly(2026, 10, 10);
        var d = IntradayRules.SelectDisplayDay(MarketState.Holiday, sat, new DateOnly(2026, 10, 8), 7);
        Assert.Equal((new DateOnly(2026, 10, 8), false), (d.TradeDate, d.IsToday));
        // 剛好 7 天前仍算(≤ 7 天)
        var edge = IntradayRules.SelectDisplayDay(MarketState.Holiday, sat, sat.AddDays(-7), 7);
        Assert.Equal(sat.AddDays(-7), edge.TradeDate);
    }

    [Fact]
    public void DisplayDay_Holiday_BeyondRetentionOrNoData_IsNull()
    {
        var sat = new DateOnly(2026, 10, 10);
        Assert.Null(IntradayRules.SelectDisplayDay(MarketState.Holiday, sat, sat.AddDays(-8), 7).TradeDate);
        var none = IntradayRules.SelectDisplayDay(MarketState.Holiday, sat, null, 7);
        Assert.Equal((null, false), (none.TradeDate, none.IsToday));
    }

    [Fact]
    public void PrevClose_OnlyWhenQuoteTradeDateEqualsDisplayDate()
    {
        Assert.Equal(149m, IntradayRules.PrevCloseFor(Mon, Mon, 149m));
        Assert.Null(IntradayRules.PrevCloseFor(Mon, Mon.AddDays(-3), 149m));
        Assert.Null(IntradayRules.PrevCloseFor(Mon, Mon, null));
    }

    // ---------- UT-09(修改):market.stale 門檻 ----------
    [Fact]
    public void StaleThreshold_WithConnections180_WithoutUnwatchedPlusStale210()
    {
        Assert.Equal(180, StaleRule.ThresholdSeconds(true, 30, 180));
        Assert.Equal(210, StaleRule.ThresholdSeconds(false, 30, 180));
    }

    [Fact]
    public void IsStale_Boundaries_210Seconds_WhenNoConnections()
    {
        var now = new DateTime(2026, 10, 5, 5, 0, 0, DateTimeKind.Utc);
        var th = StaleRule.ThresholdSeconds(false, 30, 180);
        Assert.False(StaleRule.IsStale(MarketState.Open, now.AddSeconds(-210), now, th));      // 剛好 210 秒不算
        Assert.True(StaleRule.IsStale(MarketState.Open, now.AddSeconds(-210.001), now, th));   // 210.001 秒才算
        var th2 = StaleRule.ThresholdSeconds(true, 30, 180);
        Assert.False(StaleRule.IsStale(MarketState.Open, now.AddSeconds(-180), now, th2));
        Assert.True(StaleRule.IsStale(MarketState.Open, now.AddSeconds(-180.001), now, th2));
    }

    [Fact]
    public void IsStale_OnlyWhenOpen_AndNeverBeforeFirstSuccess()
    {
        var now = new DateTime(2026, 10, 5, 5, 0, 0, DateTimeKind.Utc);
        Assert.False(StaleRule.IsStale(MarketState.Closed, now.AddHours(-5), now, 180));
        Assert.False(StaleRule.IsStale(MarketState.Holiday, now.AddHours(-5), now, 180));
        Assert.False(StaleRule.IsStale(MarketState.Open, null, now, 180));
    }
}
