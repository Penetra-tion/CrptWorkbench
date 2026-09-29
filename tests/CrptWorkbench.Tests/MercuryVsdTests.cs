using System.Net;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class MercuryVsdTests
{
    private const string Uid = "8332-6981-984B-455F-881A-A48E-7C9A-3586";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("83326981984B455F881AA48E7C9A3586")]
    [InlineData("83326981-984B-455F-881A-A48E7C9A3586")]
    [InlineData("8332-6981-984B-455F-881A-A48E-7C9A-3586")]
    public async Task ValidFormatsUseOneFixedReadRequest(string input)
    {
        var client = new CaptureClient();
        await new MercuryVsdInfoHandler(client).ExecuteAsync(new Dictionary<string, string> { ["uid"] = input },
            new(new CrptProfile(), "correlation"), Ct);
        Assert.Equal($"https://mercury.vetrf.ru/pub/operatorui?_action=findVetDocumentFormByUuid&uuid={Uid}", client.Request!.Url);
        Assert.Equal("correlation", client.Request.Headers["X-Correlation-Id"]);
        Assert.Null(client.Request.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8332-6981-984B-455F-881A-A48E-7C9A-358")]
    [InlineData("8332-6981-984B-455F-881A-A48E-7C9A-358Z")]
    [InlineData("8332-6981-984B-455F-881A-A48E-7C9A-3586&x=1")]
    public async Task InvalidUidDoesNotReachClient(string input)
    {
        var client = new CaptureClient();
        await Assert.ThrowsAsync<ArgumentException>(() => new MercuryVsdInfoHandler(client).ExecuteAsync(
            new Dictionary<string, string> { ["uid"] = input }, new(new CrptProfile(), "c"), Ct));
        Assert.Null(client.Request);
    }

    [Fact]
    public void ParserExtractsOnlyRequestedFieldsAndRetainsLists()
    {
        var html = "<h1>Просмотр информации о ВСД</h1>" + Row("Статус", "НЕИЗВЕСТНЫЙ") +
            Row("Уникальный идентификатор ВСД", Uid) + Row("Дата оформления", "25.09.2026 01:12:57 МСК") +
            Row("Номер производственной партии", "<ul><li>2509</li><li>2510</li></ul>") +
            Row("GTIN", "04670011191828") + Row("Название продукции", "Масло") +
            Row("Объём", "50.4 кг") + Row("Годен до", "30.10.2026 - 02.11.2026") +
            Row("Упаковка", "<ul><li>Потребительский уровень</li><li>Штука (PP), 280 шт.</li></ul>") +
            Row("Контактный телефон", "123456789");
        var parsed = MercuryVsd.Parse(html, Uid);
        Assert.Equal(200, parsed.Status);
        var result = Assert.IsType<MercuryVsdInfoResult>(MercuryVsdInfoResult.Parse(parsed.Json));
        Assert.Equal("НЕИЗВЕСТНЫЙ", result.Status);
        Assert.Equal(["2509", "2510"], result.BatchNumbers);
        Assert.Equal(["Потребительский уровень", "Штука (PP), 280 шт."], result.Packages);
        Assert.Equal("30.10.2026 - 02.11.2026", result.Expiry);
        Assert.DoesNotContain("123456789", parsed.Json);
        Assert.DoesNotContain("<", parsed.Json);
    }

    [Fact]
    public void NotFoundAndUnknownPageAreDifferent()
    {
        Assert.Equal(404, MercuryVsd.Parse("Ветеринарный сопроводительный документ с указанным кодом не найден", Uid).Status);
        Assert.Equal(502, MercuryVsd.Parse("<html>Other page</html>", Uid).Status);
        Assert.Equal(502, MercuryVsd.Parse(Row("Статус", "оформлен") + Row("Уникальный идентификатор ВСД", "OTHER"), Uid).Status);
    }

    [Fact]
    public void GuardAllowsOnlyExactPublicRead()
    {
        using var allowed = new HttpRequestMessage(HttpMethod.Get, $"https://mercury.vetrf.ru/pub/operatorui?_action=findVetDocumentFormByUuid&uuid={Uid}");
        Assert.True(MercuryVsd.IsAllowed(allowed));
        foreach (var url in new[] { $"http://mercury.vetrf.ru/pub/operatorui?_action=findVetDocumentFormByUuid&uuid={Uid}",
            $"https://evil.example/pub/operatorui?_action=findVetDocumentFormByUuid&uuid={Uid}",
            $"https://mercury.vetrf.ru/pub/operatorui?_action=findVetDocumentFormByUuid&uuid={Uid}&x=1",
            "https://mercury.vetrf.ru/pub/operatorui?_action=delete" })
        {
            using var rejected = new HttpRequestMessage(HttpMethod.Get, url);
            Assert.False(MercuryVsd.IsAllowed(rejected));
        }
        using var post = new HttpRequestMessage(HttpMethod.Post, allowed.RequestUri);
        Assert.False(MercuryVsd.IsAllowed(post));
    }

    [Fact]
    public async Task MockIsLocalAndReturnsStructuredResult()
    {
        var response = await new MercuryVsdInfoHandler(new MockMercuryClient()).ExecuteAsync(
            new Dictionary<string, string> { ["uid"] = MercuryVsdInfoHandler.ExampleUid }, new(new CrptProfile(), "c"), Ct);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("true", response.ResponseHeaders["X-CRPT-Workbench-Mock"]);
        Assert.NotNull(MercuryVsdInfoResult.Parse(response.ResponseBody));
    }

    [Fact]
    public async Task SavedResultUsesStructuredResponseWithoutRequestingAgain()
    {
        var response = await new MercuryVsdInfoHandler(new MockMercuryClient()).ExecuteAsync(
            new Dictionary<string, string> { ["uid"] = MercuryVsdInfoHandler.ExampleUid }, new(new CrptProfile(), "c"), Ct);
        var view = SavedResultView.From(new ApiRequestLog
        {
            OperationId = MercuryVsdInfoHandler.OperationId, Success = true, ResponseBody = response.ResponseBody
        });
        Assert.Equal("Масло сливочное (тестовый пример)", view.MercuryVsd?.ProductName);
    }

    private static string Row(string name, string value) =>
        $"<div class=\"profile-info-row\"><div class=\"profile-info-name\">{name}</div><div class=\"profile-info-value\">{value}</div></div>";

    private sealed class CaptureClient : IMercuryClient
    {
        public IntegrationRequest? Request { get; private set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new IntegrationExchange(200, "GET", request.Url, request.Headers, null,
                new Dictionary<string, string>(), JsonSerializer.Serialize(new { uid = Uid }), TimeSpan.Zero));
        }
    }
}
