using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace StockInventory.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<HoldingChange> HoldingChanges => Set<HoldingChange>();
    public DbSet<QuoteRow> Quotes => Set<QuoteRow>();
    public DbSet<QuoteIntradayRow> QuoteIntraday => Set<QuoteIntradayRow>();
    public DbSet<MarketHoliday> MarketHolidays => Set<MarketHoliday>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(50).HasDefaultValue("");
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.MustChangePassword).HasDefaultValue(true);
            e.Property(x => x.CreatedAtUtc).HasColumnType("datetime2");
        });

        b.Entity<Instrument>(e =>
        {
            e.ToTable("Instruments", t =>
            {
                t.HasCheckConstraint("CK_Instruments_Market", "[Market] IN (1, 2)");
                t.HasCheckConstraint("CK_Instruments_Kind", "[Kind] IN (1, 2)");
            });
            e.HasKey(x => x.Symbol).HasName("PK_Instruments");
            e.Property(x => x.Symbol).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.Name).HasMaxLength(50);
            e.Property(x => x.Market).HasConversion<byte>();
            e.Property(x => x.Kind).HasConversion<byte>();
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2");
            e.HasIndex(x => x.Name).HasDatabaseName("IX_Instruments_Name");
        });

        b.Entity<UserSettings>(e =>
        {
            e.ToTable("UserSettings", t =>
            {
                t.HasCheckConstraint("CK_UserSettings_Color", "[ColorScheme] IN (0, 1)");
                t.HasCheckConstraint("CK_UserSettings_FeeRate", "[FeeRate] >= 0 AND [FeeRate] <= 0.01");
                t.HasCheckConstraint("CK_UserSettings_Discount", "[FeeDiscount] >= 0.001 AND [FeeDiscount] <= 1");
            });
            e.HasKey(x => x.UserId).HasName("PK_UserSettings");
            e.Property(x => x.ColorScheme).HasDefaultValue((byte)0);
            e.Property(x => x.DeductFees).HasDefaultValue(false);
            e.Property(x => x.FeeRate).HasColumnType("decimal(7,6)").HasDefaultValue(0.001425m);
            e.Property(x => x.FeeDiscount).HasColumnType("decimal(4,3)").HasDefaultValue(1.000m);
            e.Property(x => x.SelectedPortfolioIds).HasMaxLength(400);
            e.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2");
            e.HasOne<AppUser>().WithOne().HasForeignKey<UserSettings>(x => x.UserId)
                .HasConstraintName("FK_UserSettings_Users").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Portfolio>(e =>
        {
            e.ToTable("Portfolios");
            e.HasKey(x => x.PortfolioId).HasName("PK_Portfolios");
            e.Property(x => x.Name).HasMaxLength(30);
            e.Property(x => x.CreatedAtUtc).HasColumnType("datetime2");
            e.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2");
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique().HasDatabaseName("UX_Portfolios_User_Name");
            e.HasIndex(x => new { x.UserId, x.SortOrder }).HasDatabaseName("IX_Portfolios_User_Sort");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId)
                .HasConstraintName("FK_Portfolios_Users").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<Holding>(e =>
        {
            e.ToTable("Holdings", t =>
            {
                t.HasCheckConstraint("CK_Holdings_Cost", "[TotalCost] >= 0");
                t.HasCheckConstraint("CK_Holdings_Shares", "[Shares] >= 1");
            });
            e.HasKey(x => x.HoldingId).HasName("PK_Holdings");
            e.Property(x => x.Symbol).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.CreatedAtUtc).HasColumnType("datetime2");
            e.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2");
            e.HasIndex(x => new { x.PortfolioId, x.Symbol }).IsUnique().HasDatabaseName("UX_Holdings_Portfolio_Symbol");
            e.HasIndex(x => x.Symbol).HasDatabaseName("IX_Holdings_Symbol");
            e.HasOne(x => x.Portfolio).WithMany(p => p.Holdings).HasForeignKey(x => x.PortfolioId)
                .HasConstraintName("FK_Holdings_Portfolios").OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Instrument).WithMany().HasForeignKey(x => x.Symbol)
                .HasConstraintName("FK_Holdings_Instruments").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<HoldingChange>(e =>
        {
            e.ToTable("HoldingChanges", t =>
                t.HasCheckConstraint("CK_HoldingChanges_Action", "[Action] IN ('A', 'U', 'D')"));
            e.HasKey(x => x.ChangeId).HasName("PK_HoldingChanges");
            e.Property(x => x.PortfolioName).HasMaxLength(30);
            e.Property(x => x.Symbol).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.Action).HasColumnType("char(1)").IsUnicode(false);
            e.Property(x => x.ChangedAtUtc).HasColumnType("datetime2");
            e.HasIndex(x => new { x.UserId, x.ChangedAtUtc }).HasDatabaseName("IX_HoldingChanges_User_Time");
        });

        b.Entity<QuoteRow>(e =>
        {
            e.ToTable("Quotes", t => t.HasCheckConstraint("CK_Quotes_Source", "[PriceSource] IN (1, 2, 3)"));
            e.HasKey(x => x.Symbol).HasName("PK_Quotes");
            e.Property(x => x.Symbol).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.LastPrice).HasColumnType("decimal(12,4)");
            e.Property(x => x.PrevClose).HasColumnType("decimal(12,4)");
            e.Property(x => x.PriceSource).HasConversion<byte>();
            e.Property(x => x.TradeDate).HasColumnType("date");
            e.Property(x => x.QuoteTimeUtc).HasColumnType("datetime2");
            e.Property(x => x.FetchedAtUtc).HasColumnType("datetime2");
            e.HasOne<Instrument>().WithOne().HasForeignKey<QuoteRow>(x => x.Symbol)
                .HasConstraintName("FK_Quotes_Instruments").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<QuoteIntradayRow>(e =>
        {
            e.ToTable("QuoteIntraday", t => t.HasCheckConstraint("CK_QuoteIntraday_Price", "[Price] > 0"));
            e.HasKey(x => new { x.Symbol, x.BucketUtc }).HasName("PK_QuoteIntraday");
            e.Property(x => x.Symbol).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.BucketUtc).HasColumnType("datetime2(0)");
            e.Property(x => x.TradeDate).HasColumnType("date");
            e.Property(x => x.Price).HasColumnType("decimal(12,4)");
            e.HasIndex(x => x.TradeDate).HasDatabaseName("IX_QuoteIntraday_TradeDate");
            e.HasOne<Instrument>().WithMany().HasForeignKey(x => x.Symbol)
                .HasConstraintName("FK_QuoteIntraday_Instruments").OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<MarketHoliday>(e =>
        {
            e.ToTable("MarketHolidays");
            e.HasKey(x => x.HolidayDate).HasName("PK_MarketHolidays");
            e.Property(x => x.HolidayDate).HasColumnType("date");
            e.Property(x => x.Description).HasMaxLength(50);
        });
    }
}
