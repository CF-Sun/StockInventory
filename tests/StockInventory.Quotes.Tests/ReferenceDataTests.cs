using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StockInventory.Core;
using StockInventory.Data;
using StockInventory.Quotes;
using Xunit;

namespace StockInventory.Quotes.Tests;

public class ReferenceDataTests
{
    // 依 2026-10-06 實測的欄位結構手寫的最小 HTML(整頁單行、<tr> 分列、單一儲存格的區段列)——不是真實頁面
    static string Row(string codeName, string isin, string cfi, string market = "上市", string industry = "") =>
        $"<tr><td bgcolor=#FAFAD2>{codeName}</td><td bgcolor=#FAFAD2>{isin}</td><td bgcolor=#FAFAD2>2003/06/30</td><td bgcolor=#FAFAD2>{market}</td><td bgcolor=#FAFAD2>{industry}</td><td bgcolor=#FAFAD2>{cfi}</td><td bgcolor=#FAFAD2></td></tr>";
    static string Section(string name) => $"<tr><td bgcolor=#D5FFD5 colspan=7 align=center><B> {name} <B> </td></tr>";
    const string FW = "　";

    static string ListedHtml() => "<html><table>" + Section("股票")
        + Row("2330" + FW + "台積電", "TW0002330008", "ESVUFR", industry: "半導體業")
        + Row("1101" + FW + "台泥", "TW0001101004", "ESVUFR", industry: "水泥工業")
        + Section("ETF") + Row("0050" + FW + "元大台灣50", "TW0000050004", "CEOGEU") + Row("00878" + FW + "國泰永續高股息", "TW0000087808", "CEOJEU")
        + Section("特別股") + Row("1101B" + FW + "台泥乙特", "TW0001101B09", "EPNRAR")
        + Section("受益證券-不動產投資信託") + Row("01001T" + FW + "土銀富邦R1", "TW00001001T8", "CBCIXU")
        + Section("ETN") + Row("020000" + FW + "富邦特選蘋果N", "TW0000200001", "CMXXXU")
        + Section("上市認購(售)權證") + Row("030001" + FW + "友達台新6A購01", "TW0000300019", "RWSCCA")
        + "</table></html>";

    static string OtcHtml() => "<html><table>" + Section("股票") + Row("6488" + FW + "環球晶", "TW0006488000", "ESVUFR", "上櫃", "半導體業")
        + Section("ETF") + Row("00679B" + FW + "元大美債20年", "TW00006679B6", "CEOIBU", "上櫃") + "</table></html>";

    [Fact]
    public void Isin_KeepsStocksAndEtfs_ByCfiCode_SplitsOnFullWidthSpace()
    {
        var r = IsinParser.Parse(ListedHtml(), Market.Twse);
        Assert.Equal(new[] { "0050", "00878", "1101", "1101B", "2330" }, r.Select(x => x.Symbol).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(InstrumentKind.Stock, r.Single(x => x.Symbol == "1101B").Kind);   // 特別股視為股票
        Assert.Equal((InstrumentKind.Etf, "元大台灣50"), (r.Single(x => x.Symbol == "0050").Kind, r.Single(x => x.Symbol == "0050").Name));
        Assert.Equal(InstrumentKind.Stock, r.Single(x => x.Symbol == "2330").Kind);
        Assert.All(r, x => Assert.Equal(Market.Twse, x.Market));
        Assert.DoesNotContain(r, x => x.Symbol is "020000" or "030001" or "01001T"); // ETN、權證、受益證券不納入
    }

    [Fact]
    public void Isin_Otc_MarkedAsTpex()
    {
        var r = IsinParser.Parse(OtcHtml(), Market.Tpex);
        Assert.Equal(2, r.Count);
        Assert.Equal(InstrumentKind.Etf, r.Single(x => x.Symbol == "00679B").Kind);
        Assert.All(r, x => Assert.Equal(Market.Tpex, x.Market));
    }

    [Fact]
    public void Isin_ErrorPageOrGarbage_YieldsNothing()
    {
        Assert.Empty(IsinParser.Parse("<html><body>503</body></html>", Market.Twse));
        Assert.Empty(IsinParser.Parse("", Market.Twse));
    }

    [Fact] // 實際頁面約 9 MB、數萬列權證,解析必須是線性時間(先前用錯誤的正規表示式曾卡住)
    public void Isin_LargePage_ParsesQuickly()
    {
        var sb = new StringBuilder("<html><table>").Append(Section("上市認購(售)權證"));
        for (var i = 0; i < 40000; i++) sb.Append(Row($"03{i:D4}" + FW + "權證" + i, "TW0000300019", "RWSCCA"));
        sb.Append(Section("股票")).Append(Row("2330" + FW + "台積電", "TW0002330008", "ESVUFR")).Append("</table></html>");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = IsinParser.Parse(sb.ToString(), Market.Twse);
        Assert.Single(r);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"解析耗時 {sw.Elapsed}");
    }

    static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    [Fact] // 真實 2026 年休市日:27 筆 − 3 筆有交易的公告 − 6 筆週末 = 18 天
    public void Holiday_RealSample_ExcludesWeekendsAndTradingNotices()
    {
        var r = HolidayParser.Parse(Sample("real_holiday_2026.json"));
        Assert.Equal(18, r.Count);
        var dates = r.Select(x => x.Date).ToHashSet();
        Assert.Contains(new DateOnly(2026, 10, 9), dates);                  // 國慶日補假(週五)
        Assert.Contains(new DateOnly(2026, 2, 12), dates);                  // 春節前市場無交易
        Assert.DoesNotContain(new DateOnly(2026, 1, 2), dates);             // 國曆新年開始交易日
        Assert.DoesNotContain(new DateOnly(2026, 2, 11), dates);            // 春節前最後交易日
        Assert.DoesNotContain(new DateOnly(2026, 2, 23), dates);            // 春節後開始交易日
        Assert.DoesNotContain(new DateOnly(2026, 10, 10), dates);           // 週六不存
        Assert.All(r, x => Assert.NotEqual(DayOfWeek.Saturday, x.Date.DayOfWeek));
        Assert.All(r, x => Assert.NotEqual(DayOfWeek.Sunday, x.Date.DayOfWeek));
    }

    [Theory] [InlineData("")] [InlineData("<html/>")] [InlineData("{}")]
    public void Holiday_BadBodies_Throw(string body) => Assert.Throws<QuoteParseException>(() => HolidayParser.Parse(body));

    // ---- 同步 ----
    sealed class FakeSource : IReferenceDataSource
    {
        public string Listed = ListedHtml(), Otc = OtcHtml(), Holidays = Sample("real_holiday_2026.json");
        public bool FailListed;
        public Task<string> GetIsinHtmlAsync(int mode, CancellationToken ct) =>
            FailListed && mode == 2 ? throw new HttpRequestException("boom") : Task.FromResult(mode == 2 ? Listed : Otc);
        public Task<string> GetHolidayJsonAsync(int year, CancellationToken ct) => Task.FromResult(Holidays);
    }

    static (ReferenceDataSync Sync, ServiceProvider Sp, FakeSource Src) Make(DateTimeOffset? now = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var sp = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName)).BuildServiceProvider();
        var src = new FakeSource();
        var sync = new ReferenceDataSync(sp.GetRequiredService<IServiceScopeFactory>(), src, Options.Create(new MarketOptions()),
            new FakeTimeProvider(now ?? new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)), NullLogger<ReferenceDataSync>.Instance);
        return (sync, sp, src);
    }

    static async Task<List<Instrument>> All(ServiceProvider sp)
    {
        using var s = sp.CreateScope();
        return await s.ServiceProvider.GetRequiredService<AppDbContext>().Instruments.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task SyncInstruments_Inserts_Updates_AndDeactivatesMissing_NeverDeletes()
    {
        var (sync, sp, src) = Make();
        Assert.True(await sync.SyncInstrumentsAsync(default));
        var first = await All(sp);
        Assert.Equal(7, first.Count);
        Assert.All(first, i => Assert.True(i.IsActive));
        Assert.Equal(Market.Tpex, first.Single(i => i.Symbol == "6488").Market);

        // 第二次:台泥消失、台積電改名、新增一檔
        src.Listed = "<html><table>" + Section("股票") + Row("2330" + FW + "台積電(改)", "TW0002330008", "ESVUFR")
            + Row("2317" + FW + "鴻海", "TW0002317005", "ESVUFR") + Section("ETF")
            + Row("0050" + FW + "元大台灣50", "TW0000050004", "CEOGEU") + Row("00878" + FW + "國泰永續高股息", "TW0000087808", "CEOJEU") + "</table></html>";
        Assert.True(await sync.SyncInstrumentsAsync(default));
        var second = await All(sp);
        Assert.Equal(8, second.Count);                                           // 沒有刪除任何一筆
        Assert.False(second.Single(i => i.Symbol == "1101").IsActive);           // 不在名單 → 停用
        Assert.Equal("台積電(改)", second.Single(i => i.Symbol == "2330").Name);
        Assert.True(second.Single(i => i.Symbol == "2317").IsActive);

        // 台泥回來 → 重新啟用
        src.Listed = ListedHtml();
        await sync.SyncInstrumentsAsync(default);
        Assert.True((await All(sp)).Single(i => i.Symbol == "1101").IsActive);
    }

    [Fact] // 防呆:來源異常(錯誤頁、截斷)時不得更動,更不能大量停用
    public async Task SyncInstruments_SourceFailureOrTruncation_ChangesNothing()
    {
        var (sync, sp, src) = Make();
        await sync.SyncInstrumentsAsync(default);

        src.Listed = "<html><body>系統維護中</body></html>";
        Assert.False(await sync.SyncInstrumentsAsync(default));
        Assert.All(await All(sp), i => Assert.True(i.IsActive));

        src.Listed = ListedHtml(); src.FailListed = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => sync.SyncInstrumentsAsync(default));
        Assert.All(await All(sp), i => Assert.True(i.IsActive));
    }

    [Fact]
    public async Task SyncHolidays_Replaces_YearsRows_AndIgnoresEmptyResult()
    {
        var (sync, sp, src) = Make();
        Assert.True(await sync.SyncHolidaysAsync(2026, default));
        Assert.True(await sync.SyncHolidaysAsync(2026, default));               // 冪等
        using (var s = sp.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(18, await db.MarketHolidays.CountAsync());
            db.MarketHolidays.Add(new MarketHoliday { HolidayDate = new DateOnly(2026, 7, 1), Description = "過時資料" });
            db.MarketHolidays.Add(new MarketHoliday { HolidayDate = new DateOnly(2025, 12, 25), Description = "去年" });
            await db.SaveChangesAsync();
        }
        await sync.SyncHolidaysAsync(2026, default);
        using (var s = sp.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(19, await db.MarketHolidays.CountAsync());              // 2026 年過時的被移除,2025 年的不受影響
            Assert.False(await db.MarketHolidays.AnyAsync(h => h.HolidayDate == new DateOnly(2026, 7, 1)));
        }

        src.Holidays = """{"stat":"ok","data":[]}""";
        Assert.False(await sync.SyncHolidaysAsync(2026, default));              // 空結果不更動
        using var s3 = sp.CreateScope();
        Assert.Equal(19, await s3.ServiceProvider.GetRequiredService<AppDbContext>().MarketHolidays.CountAsync());
    }

    [Fact] // 啟動時:Instruments 為空就同步;缺當年休市日就同步;已有資料則不重抓
    public async Task Startup_SyncsOnlyWhatIsMissing()
    {
        var (sync, sp, src) = Make();
        await sync.EnsureStartupDataAsync(default);
        Assert.Equal(7, (await All(sp)).Count);
        using (var s = sp.CreateScope())
            Assert.Equal(18, await s.ServiceProvider.GetRequiredService<AppDbContext>().MarketHolidays.CountAsync());

        src.Listed = "壞掉"; src.Holidays = "壞掉";
        await sync.EnsureStartupDataAsync(default);                              // 都已有資料 → 不會去抓,也就不會失敗
        Assert.Equal(7, (await All(sp)).Count);
    }
}

public class IsinRealSampleTests // 真實頁面開頭(2026-10-06,使用者從 Q02_isin_listed.bin 貼出)
{
    [Fact]
    public void RealPageHead_Parses15Stocks_WithHeaderAndSectionRowsIgnored()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "real_isin_listed_head.html"));
        var r = IsinParser.Parse(html, Market.Twse);
        Assert.Equal(15, r.Count);
        Assert.All(r, x => Assert.Equal((InstrumentKind.Stock, Market.Twse), (x.Kind, x.Market)));
        Assert.Equal("台泥", r.Single(x => x.Symbol == "1101").Name);
        Assert.Equal("愛之味", r.Single(x => x.Symbol == "1217").Name);
        Assert.DoesNotContain(r, x => x.Name.Contains("CFICode") || x.Symbol.Contains("有價證券"));
    }

    [Fact] // 標籤大小寫不影響解析
    public void UppercaseTags_AreParsed()
    {
        var html = "<TABLE><TR><TD>2330　台積電</TD><TD>TW0002330008</TD><TD>1994/09/05</TD><TD>上市</TD><TD>半導體業</TD><TD>ESVUFR</TD><TD></TD></TR></TABLE>";
        Assert.Single(IsinParser.Parse(html.Replace("<TR>", "<tr>"), Market.Twse)); // 列起點以 <tr> 切(實際頁面為小寫)
    }
}
