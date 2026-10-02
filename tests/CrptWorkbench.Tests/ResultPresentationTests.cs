using System.Net;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Web.Components;
using CrptWorkbench.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class ResultPresentationTests
{
    [Fact]
    public async Task WaterLicencesAppearOnceBesideWellAndWithoutPermitDocuments()
    {
        var withWell = new CisInfo
        {
            RequestedCis = "water-code",
            CertDoc = [new() { Type = "OTHER", WellNumber = "49" }],
            Licences = [new() { LicenceNumber = "МОС06318ВЭ", LicenceDate = "2025-05-20" },
                new() { LicenceNumber = "ТВЕ12345ВЭ", LicenceDate = "2024-04-10" }]
        };
        var html = await Render<CodeInfoResultView>(new CodeInfoResults([new(withWell)]));
        Assert.Contains("Скважина: 49", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "Лицензия: МОС06318ВЭ").Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("Дата лицензии: 20.05.2025", html);
        Assert.Contains("Лицензия: ТВЕ12345ВЭ", html);

        html = await Render<CodeInfoResultView>(new CodeInfoResults([new(withWell with
        {
            CertDoc = null,
            Licences = [new() { LicenceNumber = "МОС06318ВЭ" }, new() { LicenceDate = "2025-05-20" }, new()]
        })]));
        Assert.Contains("Разрешительные документы", html);
        Assert.Contains("Лицензия: МОС06318ВЭ", html);
        Assert.Contains("Дата лицензии: 20.05.2025", html);
        Assert.DoesNotContain("Скважина:", html);
    }

    [Fact]
    public async Task PermitCardLinksOnlySupportedNumbersAndOmitsStatusGroup()
    {
        const string number = "RU Д-DE.АА00.В.00001/25";
        var html = await Render<CodeInfoResultView>(new CodeInfoResults([new(new CisInfo
        {
            RequestedCis = "code-1",
            CertDoc = [new() { Type = "CONFORMITY_DECLARATION", Number = number, StatusGroup = 1 },
                new() { Type = "CONFORMITY_CERTIFICATE", Number = "invalid-number", StatusGroup = 2 }]
        })]));
        Assert.Contains("title=\"Проверить документ в реестре\"", html);
        Assert.Contains(number, html);
        Assert.DoesNotContain("Проверить документ ↗", html);
        Assert.Contains("invalid-number", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"permit-document permit-document-link\"").Cast<System.Text.RegularExpressions.Match>());
        Assert.DoesNotContain("Зелёный статус", html);
        Assert.DoesNotContain("Жёлтый статус", html);
        Assert.DoesNotContain("группа 1", html);
    }

    [Fact]
    public async Task GtinIsClickableOnlyWhenCodeProvidesAValue()
    {
        var present = await Render<CodeInfoResultView>(new CodeInfoResults(
            [new(new CisInfo { RequestedCis = "code-1", Gtin = "04600000000000" })]));
        Assert.Contains("title=\"Запросить товар в Национальном каталоге\"", present);
        Assert.Contains("class=\"code-field-link\"", present);
        Assert.Contains("04600000000000</button>", present);

        var absent = await Render<CodeInfoResultView>(new CodeInfoResults(
            [new(new CisInfo { RequestedCis = "code-2" })]));
        Assert.DoesNotContain("Запросить товар в Национальном каталоге", absent);
    }

    [Fact]
    public async Task VsdAppearsOnlyWhenPresentAndLinksOnlyForMercuryUid()
    {
        const string uid = "8332-6981-984B-455F-881A-A48E-7C9A-3586";
        var linked = CodeInfoResults.Parse($"[{{\"cisInfo\":{{\"requestedCis\":\"code-1\",\"vsd\":\"{uid}\"}}}}]")!;
        var linkedHtml = await Render<CodeInfoResultView>(linked);
        Assert.Contains("<dt>ВСД</dt>", linkedHtml);
        Assert.Contains("title=\"Запросить ВСД в Меркурии\"", linkedHtml);
        Assert.Contains($">{uid}</button>", linkedHtml);

        var number = CodeInfoResults.Parse("[{\"cisInfo\":{\"requestedCis\":\"code-2\",\"vsd\":\"12345\"}}]")!;
        var numberHtml = await Render<CodeInfoResultView>(number);
        Assert.Contains("<dt>ВСД</dt>", numberHtml);
        Assert.DoesNotContain("Запросить ВСД в Меркурии", numberHtml);

        var absent = await Render<CodeInfoResultView>(new CodeInfoResults([new(new CisInfo { RequestedCis = "code-3" })]));
        Assert.DoesNotContain("<dt>ВСД</dt>", absent);
    }

    [Theory]
    [InlineData("RETIRED", "DEFECT", null, "Повреждение или брак")]
    [InlineData("WRITTEN_OFF", "OTHER", "Повреждение", "Повреждение")]
    [InlineData("WITHDRAWN", null, null, "Не указана оператором")]
    public async Task RetiredCodesShowWithdrawalReason(string status, string? reason, string? other, string expected)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new[] { new { cisInfo = new { status, withdrawReason = reason, withdrawReasonOther = other } } });
        var results = CodeInfoResults.Parse(body)!;
        var html = await Render<CodeInfoResultView>(results);
        Assert.Contains("Причина вывода", html);
        Assert.Contains(expected, html);
    }

    [Theory]
    [InlineData("INTRODUCED", "tone-success")]
    [InlineData("EMITTED", "tone-info")]
    [InlineData("APPLIED", "tone-info")]
    [InlineData("APPLIED_NOT_PAID", "tone-warning")]
    [InlineData("RETIRED", "tone-neutral")]
    [InlineData("WITHDRAWN", "tone-neutral")]
    [InlineData("WRITTEN_OFF", "tone-neutral")]
    [InlineData("DISAGGREGATION", "tone-neutral")]
    [InlineData("DISAGGREGATED", "tone-neutral")]
    [InlineData("FUTURE", "tone-neutral")]
    [InlineData(null, "tone-neutral")]
    public void CodeLifecycleHasItsOwnAppearance(string? status, string expected) =>
        Assert.Equal(expected, ResultStatusAppearance.ForCode(new(new CisInfo { Status = status })).CssClass);

    [Theory]
    [InlineData("CHECKED_OK", "tone-success")]
    [InlineData("CHECKED_NOT_OK", "tone-error")]
    [InlineData("PARSE_ERROR", "tone-error")]
    [InlineData("PROCESSING_ERROR", "tone-error")]
    [InlineData("IN_PROGRESS", "tone-warning")]
    [InlineData("WAIT_FOR_CONTINUATION", "tone-warning")]
    [InlineData("WAIT_ACCEPTANCE", "tone-warning")]
    [InlineData("WAIT_PARTICIPANT_REGISTRATION", "tone-warning")]
    [InlineData("ACCEPTED", "tone-success")]
    [InlineData("CANCELLED", "tone-neutral")]
    [InlineData("FUTURE", "tone-neutral")]
    [InlineData(null, "tone-neutral")]
    public void DocumentStatusHasItsOwnAppearance(string? status, string expected) =>
        Assert.Equal(expected, ResultStatusAppearance.ForDocument(Document(status)).CssClass);

    [Fact]
    public void ErrorsOverrideOtherwiseSuccessfulStatuses()
    {
        Assert.Equal("tone-error", ResultStatusAppearance.ForCode(new(new CisInfo { Status = "INTRODUCED" }, "404")).CssClass);
        Assert.Equal("tone-error", ResultStatusAppearance.ForCode(new(ErrorMessage: "Ошибка")).CssClass);
        Assert.Equal("tone-error", ResultStatusAppearance.ForCode(new()).CssClass);
        Assert.Equal("tone-error", ResultStatusAppearance.ForDocument(Document("CHECKED_OK") with { Errors = ["Ошибка"] }).CssClass);
        Assert.Equal("tone-error", ResultStatusAppearance.ForDocument(Document("CHECKED_OK") with { CommonErrors = [new("Ошибка")] }).CssClass);
        Assert.Equal("tone-success", ResultStatusAppearance.ForDocument(Document("CHECKED_OK") with { Errors = [" ", ""], CommonErrors = [new(null)] }).CssClass);
    }

    [Fact]
    public async Task DocumentRendersActualStatusAndOnlyNonemptyUniqueErrors()
    {
        var item = Document("CHECKED_OK") with { Errors = ["Ошибка", " "], CommonErrors = [new("Ошибка"), new("Другая ошибка")] };
        var html = await Render<DocumentStatusResultView>(new DocumentStatusResults([item]));
        Assert.Contains("document-status-banner tone-error", html);
        Assert.Contains("Обработан", html);
        Assert.Contains("<li>Ошибка</li><li>Другая ошибка</li>", html);
        Assert.Contains("result-errors", html);
        html = await Render<DocumentStatusResultView>(new DocumentStatusResults([Document("CHECKED_OK")]));
        Assert.Contains("document-status-banner tone-success", html);
        Assert.DoesNotContain("result-errors", html);
        html = await Render<DocumentStatusResultView>(new DocumentStatusResults([]));
        Assert.Contains("Документ не найден", html);
        Assert.DoesNotContain("document-status-banner", html);
    }

    [Fact]
    public async Task CodeCardsShowCoreDetailsAndKeepOnlyLargeListsCollapsed()
    {
        var item = new CodeInfoItem(new CisInfo
        {
            RequestedCis = "requested-code", Cis = "returned-code", GeneralPackageType = "BOX",
            Status = "INTRODUCED", OwnerName = "Владелец", OwnerInn = "1234567890",
            ProductGroup = "Молочная продукция", ApplicationDate = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero),
            Expirations = [new() { ExpirationStorageDate = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), StorageConditionName = "Хранить в холоде" }],
            CertDoc = [new() { Type = "CONFORMITY_DECLARATION", Number = "permit-123", Date = "2026-09-18" }],
            Child = ["child-code"], Parent = "parent-code"
        });
        var html = await Render<CodeInfoResultView>(new CodeInfoResults([item]));
        Assert.Contains("code-result-card", html);
        Assert.DoesNotContain("<table", html);
        Assert.DoesNotContain("демонстрационные", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<details\\b").Cast<System.Text.RegularExpressions.Match>());
        Assert.DoesNotContain("<details class=\"code-detail-disclosure code-bottom-disclosure\" open", html);
        Assert.DoesNotContain("Подробные сведения", html);
        foreach (var value in new[] { "requested-code", "returned-code", "child-code", "parent-code", "1234567890",
            "Состав упаковки · 1", "Не возвращается для этого типа", "Молочная продукция", "Хранить в холоде",
            "Разрешительные документы", "permit-123", "20.09.2026", "01.10.2026" })
            Assert.Contains(value, html);
        Assert.DoesNotContain("<summary class=\"disclosure-toggle\">Даты и сроки</summary>", html);
        Assert.DoesNotContain("<summary class=\"disclosure-toggle\">Разрешительные документы</summary>", html);
        Assert.DoesNotContain("Оплата кода", html);
        Assert.True(html.IndexOf("parent-code", StringComparison.Ordinal) < html.IndexOf("Состав упаковки", StringComparison.Ordinal));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\\\"related-code-link\\\"").Count);
        Assert.Contains("<code>child-code</code></button>", html);
        Assert.Contains("<code>parent-code</code></button>", html);
        html = await Render<CodeInfoResultView>(new CodeInfoResults([item, new(new CisInfo { RequestedCis = "missing-code" }, "404", "Код не найден")]));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<details\\b").Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("metric-errors", html);
        Assert.Contains("missing-code", html);
        Assert.Contains("Ошибка 404", html);
        Assert.Contains("Код не найден", html);
        Assert.Contains("Информация получена частично", html);
    }

    [Fact]
    public async Task CodeCardShowsPaymentAndDocumentParticipants()
    {
        var item = new CodeInfoItem(new CisInfo { RequestedCis = "paid-code", Status = "INTRODUCED", GeneralPackageType = "UNIT" })
        {
            DocumentHistory = new(true,
                [new("document-id", new DateTimeOffset(2026, 9, 24, 3, 54, 0, TimeSpan.Zero),
                    "PAYMENT", "ACCEPTED", "Компания", "1234567890", null, null)])
        };
        var html = await Render<CodeInfoResultView>(new CodeInfoResults([item]));
        Assert.Contains("Оплачен", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<details\\b").Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("<summary class=\"disclosure-toggle\">Документы по коду · 1</summary>", html);
        Assert.Contains("<table class=\"code-history-table\"", html);
        Assert.Contains("document-id", html);
        Assert.Contains("Компания · ИНН 1234567890", html);
        Assert.Contains("24.09.2026 03:54 UTC", html);
    }

    [Theory]
    [InlineData("UNIT", true)]
    [InlineData("GROUP", true)]
    [InlineData("SET", true)]
    [InlineData("BOX", false)]
    [InlineData("ATK", false)]
    public async Task PaymentOnlyAppearsForEligibleCodeTypes(string type, bool visible)
    {
        var item = new CodeInfoItem(new CisInfo { RequestedCis = "code", GeneralPackageType = type })
        {
            DocumentHistory = new(false, [])
        };
        var html = await Render<CodeInfoResultView>(new CodeInfoResults([item]));
        Assert.Equal(visible, html.Contains("<dt>Оплата кода</dt><dd>Не оплачен</dd>"));
        Assert.DoesNotContain("<details", html);
    }

    [Fact]
    public async Task NationalCatalogHidesRequestedGtinAsSingleUnitPackage()
    {
        var product = new NationalCatalogProductResult("Товар", "04600000000000", "1234", "Обувь", "Опубликована",
            [
                new("04600000000000", "Потребительская", 1),
                new("04600000000017", "Короб", 10)
            ],
            [], null);

        var html = await RenderNationalCatalogProduct(product);

        Assert.Contains("GTIN упаковок", html);
        Assert.Contains("04600000000017", html);
        Assert.Contains("Короб", html);
        Assert.DoesNotContain("<td>04600000000000</td><td>Потребительская</td><td>1</td>", html);
    }

    private static DocumentStatusItem Document(string? status) => new("id", null, null, null, status, null, null);

    private static async Task<string> Render<T>(object results) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Results"] = results }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }

    private static async Task<string> RenderNationalCatalogProduct(NationalCatalogProductResult result)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<NationalCatalogProductResultView>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Result"] = result }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
}
