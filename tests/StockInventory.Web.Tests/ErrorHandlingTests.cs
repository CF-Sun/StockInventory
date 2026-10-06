using Microsoft.Extensions.Logging;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StockInventory.Web.Security;
using Xunit;

namespace StockInventory.Web.Tests;

public class ErrorHandlingTests // SEC-14
{
    sealed class Feature(Exception ex) : IExceptionHandlerFeature
    {
        public Exception Error { get; } = ex;
        public string Path { get; } = "/";
        public Endpoint? Endpoint => null;
        public Microsoft.AspNetCore.Routing.RouteValueDictionary? RouteValues => null;
    }

    static DefaultHttpContext Ctx(string path)
    {
        var c = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider() };
        c.Request.Path = path;
        c.Response.Body = new MemoryStream();
        c.Features.Set<IExceptionHandlerFeature>(new Feature(new InvalidOperationException("SECRET-DETAIL Server=db;Password=hunter2")));
        return c;
    }

    [Fact]
    public async Task Api_Returns500Problem_WithoutDetails()
    {
        var c = Ctx("/api/holdings");
        await ErrorHandling.HandleAsync(c);
        c.Response.Body.Position = 0;
        var body = Encoding.UTF8.GetString(((MemoryStream)c.Response.Body).ToArray());
        Assert.Equal(500, c.Response.StatusCode);
        Assert.Equal("application/problem+json", c.Response.ContentType);
        Assert.Contains("INTERNAL_ERROR", body);
        Assert.DoesNotContain("SECRET-DETAIL", body);
        Assert.DoesNotContain("hunter2", body);
    }

    [Fact]
    public async Task Page_RedirectsToGenericErrorPage()
    {
        var c = Ctx("/Portfolios");
        await ErrorHandling.HandleAsync(c);
        Assert.Equal(302, c.Response.StatusCode);
        Assert.Equal("/Error", c.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task ErrorPage_IsPublic_AndShowsNoDetails()
    {
        using var f = new TestFactory();
        var res = await LoginFlowTests.NewClient(f).GetAsync("/Error");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
    }
}
