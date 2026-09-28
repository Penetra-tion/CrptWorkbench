using System.Net;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Web;
using CrptWorkbench.Web.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class OperationNavigationTests
{
    private static readonly ApiOperationDescriptor[] Operations =
    [
        new(TrueApiCodeInfoHandler.OperationId, "Коды", ApiSystem.TrueApi, "POST", "", typeof(object), typeof(object), []),
        new(NationalCatalogProductHandler.OperationId, "Товар", ApiSystem.NationalCatalog, "GET", "", typeof(object), typeof(object), [])
    ];

    [Fact]
    public void CatalogLinkSelectsRequestedFormWithoutCodeExecution()
    {
        var url = OperationNavigation.ConsoleUrl(NationalCatalogProductHandler.OperationId);
        var destination = OperationNavigation.ResolveConsole(new Uri("https://localhost" + url).Query, Operations);
        Assert.Same(Operations[1], destination.Operation);
        Assert.Null(destination.Code);
        Assert.False(destination.UnknownOperation);
        Assert.Same(Operations[1], OperationNavigation.ResolveConsole("?operation=" + NationalCatalogProductHandler.OperationId.ToUpperInvariant(), Operations).Operation);
    }

    [Fact]
    public void UnknownOrEmptyExplicitOperationHasNoFallbackForm()
    {
        foreach (var query in new[] { "?operation=missing", "?operation=", "?operation=a&operation=b" })
        {
            var destination = OperationNavigation.ResolveConsole(query, Operations);
            Assert.True(destination.UnknownOperation);
            Assert.Null(destination.Operation);
            Assert.Null(destination.Code);
        }
        Assert.False(OperationNavigation.ResolveConsole("", Operations).UnknownOperation);
    }

    [Fact]
    public void ExistingEncodedCodeLinkTakesPrecedenceEvenOverUnknownOperation()
    {
        const string code = "010460000000000021a+b/=?&";
        foreach (var operation in new[] { "missing", NationalCatalogProductHandler.OperationId })
        {
            var destination = OperationNavigation.ResolveConsole($"?operation={operation}&code={Uri.EscapeDataString(code)}", Operations);
            Assert.Same(Operations[0], destination.Operation);
            Assert.Equal(code, destination.Code);
            Assert.False(destination.UnknownOperation);
        }
        Assert.Same(Operations[1], OperationNavigation.ResolveConsole($"?code=%20&operation={NationalCatalogProductHandler.OperationId}", Operations).Operation);
    }

    [Theory]
    [InlineData("/operations", true)]
    [InlineData("/console?operation=some-operation", true)]
    [InlineData("/package-aggregation", true)]
    [InlineData("/package-transformation", true)]
    [InlineData("/code-correction", true)]
    [InlineData("/special-features", true)]
    [InlineData("/history", false)]
    [InlineData("/settings", false)]
    [InlineData("/console-other", false)]
    public async Task SidebarHighlightsOnlyOperationPages(string path, bool expectedActive)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<NavigationManager>(new TestNavigation(path)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<OperationsNavLink>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(OperationsNavLink.AdditionalAttributes)] = new Dictionary<string, object> { ["href"] = "/operations" },
                [nameof(OperationsNavLink.ChildContent)] = (RenderFragment)(builder => builder.AddContent(0, "Операции"))
            }));
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
        Assert.Equal(expectedActive, html.Contains("class=\"active\"", StringComparison.Ordinal));
        Assert.Equal(expectedActive, html.Contains("aria-current=\"page\"", StringComparison.Ordinal));
        Assert.Contains("href=\"/operations\"", html);
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string path) => Initialize("https://localhost/", "https://localhost" + path);
    }
}
