namespace StockInventory.Core;

public static class MarketCalendar
{
    /// <param name="taipeiNow">已換算為 Taipei 的本地時間(由呼叫端轉換,Core 不讀時鐘)。</param>
    public static MarketState GetState(DateTime taipeiNow, ISet<DateOnly> holidays, TimeSpan open, TimeSpan close)
    {
        var date = DateOnly.FromDateTime(taipeiNow);
        if (taipeiNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || holidays.Contains(date))
            return MarketState.Holiday;
        var t = taipeiNow.TimeOfDay;
        return t >= open && t <= close ? MarketState.Open : MarketState.Closed;
    }
}
