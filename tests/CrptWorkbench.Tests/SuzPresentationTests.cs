using System.Net;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Web;
using CrptWorkbench.Web.Components;
using CrptWorkbench.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzPresentationTests
{
    [Theory]
    [InlineData("REPORT_UTILIZE_RECEIVED", "Получение отчёта о нанесении КМ")]
    [InlineData("REPORT_UTILIZE_VALIDATION", "Отправка на проверку метаданных отчёта в МДЛП")]
    [InlineData("REPORT_UTILIZE_VALIDATION_RESULT", "Результат проверки метаданных отчёта в МДЛП")]
    [InlineData("REPORT_UTILIZE_EMISSION_SEND", "Отправка отчёта в РЭ/РЭМ")]
    [InlineData("RNMS_GIS_ROUTED", "Отправка отчёта на обработку")]
    [InlineData("RNMS_GIS_PROCESSED", "Результат обработки отчёта")]
    [InlineData("FUTURE_OPERATION", "FUTURE_OPERATION")]
    public void OperationNamesAreLocalizedWithUnknownFallback(string operation, string expected)
    {
        Assert.Equal(expected, SuzDocumentStatusResults.Operation(operation));
    }

    [Theory]
    [InlineData("SUCCESS", "tone-success")]
    [InlineData("FAILED", "tone-error")]
    [InlineData("IN_PROGRESS", "tone-warning")]
    [InlineData("PARTIALLY", "tone-warning")]
    [InlineData("FUTURE", "tone-neutral")]
    public void ReceiptColorsFollowDocumentState(string state, string css)
    {
        var receipt = new SuzReceipt("receipt", 0, "document", 0, state, 0, "description", null, null, null, null);
        Assert.Equal(css, ResultStatusAppearance.ForSuzDocument(receipt).CssClass);
    }

    [Fact]
    public async Task CardsRenderMultipleReceiptsAndEmptyState()
    {
        var exchange = await new SuzDocumentStatusHandler(new MockSuzClient()).ExecuteAsync(
            new Dictionary<string, string> { ["documentId"] = "00000000-0000-0000-0000-000000000005" },
            new(new CrptProfile(), "render"), TestContext.Current.CancellationToken);
        var results = SuzDocumentStatusResults.Parse(exchange.ResponseBody)!;
        var html = await Render<SuzDocumentStatusResultView>(new() { ["Results"] = results });
        Assert.Contains("Обработан успешно", html);
        Assert.Contains("В обработке", html);
        Assert.Contains("10000000-0000-0000-0000-000000000001", html);
        Assert.Contains("10000000-0000-0000-0000-000000000002", html);
        Assert.Contains("Этапы обработки (2)", html);
        Assert.Contains("Получение отчёта о нанесении КМ", html);
        Assert.Contains("Результат обработки отчёта", html);
        Assert.DoesNotContain("REPORT_UTILIZE_RECEIVED", html);
        Assert.DoesNotContain("RNMS_GIS_PROCESSED", html);
        Assert.Contains("<details", html);
        Assert.Contains("Квитанции не найдены", await Render<SuzDocumentStatusResultView>(new() { ["Results"] = new SuzDocumentStatusResults([]) }));
    }

    [Fact]
    public async Task CatalogShowsSuzDocumentStatusWithoutLegacyOrder()
    {
        IApiOperationHandler[] handlers = [new SuzDocumentStatusHandler(new MockSuzClient())];
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IApiOperationRegistry>(new ApiOperationRegistry(handlers))
            .AddSingleton(new WorkbenchContext())
            .AddSingleton<IFavoriteApiOperationRepository>(new EmptyFavoriteRepository())
            .AddSingleton<FavoriteApiOperationService>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => WebUtility.HtmlDecode((await renderer.RenderComponentAsync<Operations>()).ToHtmlString()));
        Assert.Contains("aria-label=\"Система API\"", html);
        Assert.Contains("СУЗ", html);
        Assert.DoesNotContain("Запрос статуса заказа", html);
        Assert.DoesNotContain("Статус документа СУЗ", html);
    }

    private sealed class EmptyFavoriteRepository : IFavoriteApiOperationRepository
    {
        public Task<IReadOnlyList<string>> ListOperationIdsAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetAsync(Guid profileId, string operationId, bool isFavorite, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void ModeSelectsSuzAdapterWithoutEnablingUnrelatedEndpoints()
    {
        var production = new ServiceCollection().AddCrptClients(new() { Mode = CrptIntegrationMode.Production });
        Assert.Equal(typeof(RealSuzClient), production.Single(x => x.ServiceType == typeof(ISuzClient)).ImplementationType);
        using var mock = new ServiceCollection().AddCrptClients(new()).BuildServiceProvider();
        Assert.IsType<MockSuzClient>(mock.GetRequiredService<ISuzClient>());
    }

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            WebUtility.HtmlDecode((await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString()));
    }
}
