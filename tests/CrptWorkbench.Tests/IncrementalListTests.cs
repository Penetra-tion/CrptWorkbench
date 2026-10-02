using CrptWorkbench.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class IncrementalListTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(200)]
    [InlineData(250)]
    public async Task FirstPortionShowsAtMostOneHundredAndCorrectRemainingButton(int count)
    {
        RenderFragment<int> row = value => builder =>
        {
            builder.OpenElement(0, "li");
            builder.AddContent(1, $"record-{value}");
            builder.CloseElement();
        };
        var html = await Render<IncrementalList<int>>(new()
        {
            [nameof(IncrementalList<int>.Items)] = Enumerable.Range(0, count).ToArray(),
            [nameof(IncrementalList<int>.ChildContent)] = row
        });
        Assert.Equal(Math.Min(100, count), System.Text.RegularExpressions.Regex.Matches(html, "<li>").Count);
        if (count > 100)
            Assert.Contains($"Показать следующие {Math.Min(100, count - 100)} записей", html);
        else
            Assert.DoesNotContain("<button", html);
        if (count > 0)
            Assert.Contains($"Показано {Math.Min(100, count)} из {count}", html);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LoadingOrDisabledBlocksTheButton(bool loading, bool disabled)
    {
        var html = await Render<ShowMore>(new()
        {
            [nameof(ShowMore.VisibleCount)] = 100L,
            [nameof(ShowMore.TotalCount)] = 250L,
            [nameof(ShowMore.HasMore)] = true,
            [nameof(ShowMore.Loading)] = loading,
            [nameof(ShowMore.Disabled)] = disabled
        });
        Assert.Contains("disabled", html);
        Assert.Contains(loading ? "Загрузка…" : "Показать следующие 100 записей", html);
    }

    [Fact]
    public async Task ItemsContentKeepsControlsOutsideTable()
    {
        RenderFragment<IReadOnlyList<int>> table = items => builder =>
        {
            builder.OpenElement(0, "table");
            builder.OpenElement(1, "tbody");
            foreach (var item in items)
            {
                builder.OpenElement(2, "tr");
                builder.OpenElement(3, "td");
                builder.AddContent(4, item);
                builder.CloseElement();
                builder.CloseElement();
            }
            builder.CloseElement();
            builder.CloseElement();
        };
        var html = await Render<IncrementalList<int>>(new()
        {
            [nameof(IncrementalList<int>.Items)] = Enumerable.Range(0, 101).ToArray(),
            [nameof(IncrementalList<int>.ItemsContent)] = table
        });
        Assert.True(html.IndexOf("</table>", StringComparison.Ordinal) < html.IndexOf("<button", StringComparison.Ordinal));
    }

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return System.Net.WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
}
