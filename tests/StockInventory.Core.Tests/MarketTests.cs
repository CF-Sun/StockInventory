using StockInventory.Core;
using Xunit;

namespace StockInventory.Core.Tests;

public class MarketTests
{
    static readonly TimeSpan O = new(8, 30, 0), C = new(14, 35, 0);
    static readonly HashSet<DateOnly> Hol = [new(2026, 10, 9)];
    static MarketState S(DateTime t) => MarketCalendar.GetState(t, Hol, O, C);

    [Theory] // UT-09 邊界
    [InlineData(8, 29, 59, MarketState.Closed)]
    [InlineData(8, 30, 0, MarketState.Open)]
    [InlineData(14, 35, 0, MarketState.Open)]
    [InlineData(14, 35, 1, MarketState.Closed)]
    public void Boundaries(int h, int m, int s, MarketState e) =>
        Assert.Equal(e, S(new DateTime(2026, 10, 5, h, m, s))); // 週一

    [Fact]
    public void WeekendAndHoliday()
    {
        Assert.Equal(MarketState.Holiday, S(new DateTime(2026, 10, 10, 10, 0, 0))); // 週六
        Assert.Equal(MarketState.Holiday, S(new DateTime(2026, 10, 9, 10, 0, 0)));
    }

    [Fact]
    public void StaleBoundary()
    {
        var now = new DateTime(2026, 10, 5, 5, 0, 0, DateTimeKind.Utc);
        QuoteInput Q(double ago) => new(10m, 10m, PriceSource.Trade, now.AddSeconds(-ago));
        Assert.Equal(QuoteStatus.Live, QuoteStatusCalculator.Get(Q(180), MarketState.Open, now, 180));
        Assert.Equal(QuoteStatus.Delayed, QuoteStatusCalculator.Get(Q(180.001), MarketState.Open, now, 180));
        Assert.Equal(QuoteStatus.Live, QuoteStatusCalculator.Get(Q(999), MarketState.Closed, now, 180));
        Assert.Equal(QuoteStatus.Missing, QuoteStatusCalculator.Get(null, MarketState.Open, now, 180));
    }
}
