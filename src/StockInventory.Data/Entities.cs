using Microsoft.AspNetCore.Identity;
using StockInventory.Core;

namespace StockInventory.Data;

public class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public class Instrument
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public Market Market { get; set; }
    public InstrumentKind Kind { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }
}

public class UserSettings
{
    public Guid UserId { get; set; }
    public byte ColorScheme { get; set; }
    public bool DeductFees { get; set; }
    public decimal FeeRate { get; set; } = 0.001425m;
    public decimal FeeDiscount { get; set; } = 1.000m;
    public string? SelectedPortfolioIds { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class Portfolio
{
    public int PortfolioId { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<Holding> Holdings { get; set; } = [];
}

public class Holding
{
    public int HoldingId { get; set; }
    public int PortfolioId { get; set; }
    public string Symbol { get; set; } = "";
    public long TotalCost { get; set; }
    public long Shares { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Portfolio? Portfolio { get; set; }
    public Instrument? Instrument { get; set; }
}

public class HoldingChange
{
    public long ChangeId { get; set; }
    public Guid UserId { get; set; }
    public int PortfolioId { get; set; }
    public string PortfolioName { get; set; } = "";
    public string Symbol { get; set; } = "";
    public char Action { get; set; }
    public long? OldTotalCost { get; set; }
    public long? OldShares { get; set; }
    public long? NewTotalCost { get; set; }
    public long? NewShares { get; set; }
    public DateTime ChangedAtUtc { get; set; }
}

public class QuoteRow
{
    public string Symbol { get; set; } = "";
    public decimal? LastPrice { get; set; }
    public decimal? PrevClose { get; set; }
    public PriceSource PriceSource { get; set; }
    public DateOnly TradeDate { get; set; }
    public DateTime? QuoteTimeUtc { get; set; }
    public DateTime FetchedAtUtc { get; set; }
}

public class MarketHoliday
{
    public DateOnly HolidayDate { get; set; }
    public string? Description { get; set; }
}
