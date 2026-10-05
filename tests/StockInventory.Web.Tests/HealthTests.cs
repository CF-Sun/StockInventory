using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StockInventory.Core;
using StockInventory.Quotes;
using Xunit;

namespace StockInventory.Web.Tests;

public class HealthTests
{
    sealed class Fixed(MarketStatusSnapshot s) : IMarketStatusSource { public MarketStatusSnapshot Get() => s; }

    static WebApplicationFactory<Program> Factory(MarketStatusSnapshot s) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", "Server=127.0.0.1,1;Database=x;Connect Timeout=1;User Id=u;Password=p;TrustServerCertificate=true");
            b.UseSetting("Logging:Directory", Path.Combine(Path.GetTempPath(), "si-test-logs"));
            b.ConfigureServices(sv => sv.AddSingleton<IMarketStatusSource>(new Fixed(s)));
        });

    [Fact]
    public async Task DbDown_Returns503_WithoutDetails()
    {
        using var f = Factory(new(MarketState.Closed, false, null));
        var res = await f.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("down", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("down", doc.RootElement.GetProperty("db").GetString());
        Assert.Equal("closed", doc.RootElement.GetProperty("quotes").GetProperty("state").GetString());
        Assert.DoesNotContain("127.0.0.1", text);
    }
}
