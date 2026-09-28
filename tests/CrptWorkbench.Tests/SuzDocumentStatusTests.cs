using System.Text.Json.Nodes;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzDocumentStatusTests
{
    private const string Id = "00000000-0000-0000-0000-000000000001";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Dictionary<string, string> Input(string id = Id) => new() { ["documentId"] = id };
    private static ApiOperationContext Context => new(new CrptProfile
    {
        SuzOmsId = Id, SuzConnectionId = "workbench-connection", SuzProductionBaseUrl = "https://suzgrid.crpt.ru",
        Inn = "1234567890", CertificateThumbprint = new('A', 40)
    }, "suz-correlation");

    [Fact]
    public async Task HandlerPassesExactReadContractAndProfileContext()
    {
        var client = new CaptureClient();
        var context = Context;
        context.Profile.SuzOmsId = " " + Id + " ";
        await new SuzDocumentStatusHandler(client).ExecuteAsync(Input(" AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE "), context, Ct);
        var request = Assert.IsType<IntegrationRequest>(client.Request);
        Assert.Equal($"api/v3/receipts/receipt?omsId={Id}&resultDocId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", request.Url);
        Assert.Equal("GET", request.Method);
        Assert.Null(request.Body);
        Assert.Equal(context.Profile.Id, request.Authentication!.ProfileId);
        Assert.Equal(context.Profile.SuzConnectionId, request.SuzConnection!.ConnectionId);
        Assert.Equal(Id, request.SuzConnection.OmsId);
        Assert.Equal(context.Profile.SuzProductionBaseUrl, request.SuzConnection.BaseUrl);
        Assert.Equal(context.CorrelationId, request.Headers["X-Correlation-Id"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.Equal(Ct, client.CancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("demo-success")]
    [InlineData("{00000000-0000-0000-0000-000000000001}")]
    [InlineData("00000000000000000000000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000001\n")]
    [InlineData("../receipt")]
    [InlineData("00000000-0000-0000-0000-000000000001&omsId=other")]
    public async Task InvalidGuidNeverReachesClient(string id)
    {
        var client = new CaptureClient();
        await Assert.ThrowsAsync<ArgumentException>(() => new SuzDocumentStatusHandler(client).ExecuteAsync(Input(id), Context, Ct));
        Assert.Null(client.Request);
        Assert.False(InputValidation.Operation(SuzDocumentStatusHandler.OperationId, Input(id)).IsValid);
    }

    [Fact]
    public async Task CancellationPreventsMockAndHandlerExecution()
    {
        var client = new CaptureClient();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SuzDocumentStatusHandler(client).ExecuteAsync(Input(), Context, cancel.Token));
        Assert.Null(client.Request);
    }

    [Theory]
    [InlineData(1, "SUCCESS", 1)]
    [InlineData(2, "FAILED", 1)]
    [InlineData(3, "IN_PROGRESS", 1)]
    [InlineData(4, "PARTIALLY", 1)]
    [InlineData(5, "SUCCESS", 2)]
    [InlineData(6, "", 0)]
    public async Task MockReturnsContractStatesWithoutConfiguredAuthentication(int number, string state, int count)
    {
        var context = new ApiOperationContext(new CrptProfile { SuzProductionBaseUrl = "https://unapproved.invalid" }, "mock-correlation");
        var exchange = await new SuzDocumentStatusHandler(new MockSuzClient()).ExecuteAsync(Input($"00000000-0000-0000-0000-{number:D12}"), context, Ct);
        var parsed = Assert.IsType<SuzDocumentStatusResults>(SuzDocumentStatusResults.Parse(exchange.ResponseBody));
        Assert.Equal(count, parsed.Results.Count);
        if (count > 0) Assert.Equal(state, parsed.Results[0].State);
        if (state is "FAILED" or "PARTIALLY") Assert.Contains("Код не принадлежит участнику.", SuzDocumentStatusResults.ErrorTexts(parsed.Results[0]));
        Assert.StartsWith("https://production.suz.mock.invalid/", exchange.RequestUrl);
        Assert.Equal("true", exchange.ResponseHeaders["X-CRPT-Workbench-Mock"]);
        Assert.Equal(context.CorrelationId, exchange.ResponseHeaders["X-Correlation-Id"]);
        Assert.True(exchange.Duration > TimeSpan.Zero);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"results\":null}")]
    [InlineData("{\"results\":[null]}")]
    [InlineData("{\"results\":[{}]}")]
    [InlineData("{\"results\":{}}")]
    public void MalformedResponseIsNotAnEmptySuccessfulResult(string body) => Assert.Null(SuzDocumentStatusResults.Parse(body));

    [Fact]
    public async Task ParserRetainsUnknownDataAndFindsNestedErrors()
    {
        var json = await Sample();
        var item = json["results"]![0]!;
        item["state"] = "FUTURE_STATE";
        item["workflow"] = "FUTURE_WORKFLOW";
        item["vendorField"] = "retained";
        item["details"] = JsonNode.Parse("""{"newData":[{"errors":["Nested error",{"errorMessage":"Another error"}]}]}""");
        json["futureRoot"] = 123;
        var parsed = Assert.IsType<SuzDocumentStatusResults>(SuzDocumentStatusResults.Parse(json.ToJsonString()));
        var receipt = Assert.Single(parsed.Results);
        Assert.Equal("retained", receipt.AdditionalFields!["vendorField"].GetString());
        Assert.Equal(123, parsed.AdditionalFields!["futureRoot"].GetInt32());
        Assert.Equal("FUTURE_STATE", SuzDocumentStatusResults.Status(receipt.State));
        Assert.Equal("FUTURE_WORKFLOW", SuzDocumentStatusResults.Workflow(receipt.Workflow));
        Assert.Equal(new[] { "Nested error", "Another error" }, SuzDocumentStatusResults.ErrorTexts(receipt));
        Assert.Contains("UTC", SuzDocumentStatusResults.Date(receipt.ResultDocDate));
        var failedHistory = SavedResultView.From(new ApiRequestLog { OperationId = SuzDocumentStatusHandler.OperationId,
            ResponseBody = json.ToJsonString(), Success = false, StatusCode = 401, Error = "Сервис вернул ошибку HTTP 401." });
        Assert.Null(failedHistory.SuzDocuments);
        Assert.Contains("401", failedHistory.Notice!);
    }

    [Fact]
    public async Task ParserRejectsMissingFieldsBadDatesAndMalformedEvents()
    {
        foreach (var field in new[] { "resultDocId", "resultDocDate", "sourceDocId", "sourceDocDate", "state", "code", "description" })
        {
            var json = await Sample();
            json["results"]![0]!.AsObject().Remove(field);
            Assert.Null(SuzDocumentStatusResults.Parse(json.ToJsonString()));
        }
        var badDate = await Sample();
        badDate["results"]![0]!["resultDocDate"] = long.MaxValue;
        Assert.Null(SuzDocumentStatusResults.Parse(badDate.ToJsonString()));
        var badEvent = await Sample();
        badEvent["results"]![0]!["operations"]![0]!.AsObject().Remove("operationDate");
        Assert.Null(SuzDocumentStatusResults.Parse(badEvent.ToJsonString()));
    }

    [Fact]
    public async Task HistoryPersistsMaskedReceiptsAndIsolatesProfilesIncludingReopening()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crpt-suz-history-{Guid.NewGuid():N}.db");
        var factory = new DbFactory(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync(Ct);
            var profiles = new ProfileRepository(factory);
            var profile = Context.Profile;
            profile.Name = "SUZ test";
            await profiles.UpsertAsync(profile, Ct);
            var history = new ApiRequestLogRepository(factory);
            IApiOperationHandler[] handlers = [new SuzDocumentStatusHandler(new MockSuzClient()), new SuzDemoHandler(new MockSuzClient())];
            var executor = new ExecuteApiOperationService(new ApiOperationRegistry(handlers), profiles, history, new SensitiveDataMasker());
            var result = await executor.ExecuteAsync(SuzDocumentStatusHandler.OperationId, Input("00000000-0000-0000-0000-000000000002"), profile.Id, Ct);
            Assert.True(result.Success); // HTTP success is distinct from document failure.
            var row = Assert.Single(await history.ListAsync(profile.Id, 10, Ct));
            Assert.True(row.DurationMs > 0);
            Assert.Equal(result.CorrelationId, row.CorrelationId);
            Assert.Contains(result.CorrelationId, row.RequestHeaders!);
            Assert.Contains("\"clientToken\": \"********\"", row.RequestHeaders!);
            Assert.Contains("Bearer ********", row.RequestHeaders!);
            Assert.DoesNotContain("mock-suz-token-not-a-secret", row.ExchangesJson!);
            Assert.Equal("FAILED", Assert.Single(SavedResultView.From(row).SuzDocuments!.Results).State);
            var reopened = new ApiRequestLogRepository(factory);
            Assert.NotNull(await reopened.GetAsync(profile.Id, row.Id, Ct));
            Assert.Null(await reopened.GetAsync(Guid.NewGuid(), row.Id, Ct));
            Assert.Empty(await reopened.ListAsync(Guid.NewGuid(), 100, Ct));
            await executor.ExecuteAsync("suz.order-status", new Dictionary<string, string> { ["value"] = "legacy-order" }, profile.Id, Ct);
            var legacy = (await history.ListAsync(profile.Id, 10, Ct)).Single(x => x.OperationId == "suz.order-status");
            Assert.Contains(SavedResultView.From(legacy).Fields, x => x.Value == "legacy-order");
            var badClient = new CaptureClient();
            var badExecutor = new ExecuteApiOperationService(new ApiOperationRegistry([new SuzDocumentStatusHandler(badClient)]), profiles, history, new SensitiveDataMasker());
            var invalid = await badExecutor.ExecuteAsync(SuzDocumentStatusHandler.OperationId, Input(), profile.Id, Ct);
            Assert.False(invalid.Success);
            Assert.Equal(200, invalid.StatusCode);
            Assert.Equal("invalid_response", invalid.Error!.Code);
            Assert.Equal("{}", invalid.ResponseBody);
            var invalidRow = (await history.ListAsync(profile.Id, 10, Ct)).Single(x => x.CorrelationId == invalid.CorrelationId);
            Assert.Contains("Некоррект", SavedResultView.From(invalidRow).Notice!, StringComparison.OrdinalIgnoreCase);
            var before = (await history.ListAsync(profile.Id, 100, Ct)).Count;
            await Assert.ThrowsAsync<InputValidationException>(() => executor.ExecuteAsync(SuzDocumentStatusHandler.OperationId, Input("invalid"), profile.Id, Ct));
            Assert.Equal(before, (await history.ListAsync(profile.Id, 100, Ct)).Count);
            var failingTransport = new RealSuzClient(new SuzTransportTests.Factory(new SuzTransportTests.Handler(
                _ => throw new HttpRequestException("sentinel-private-clientToken"))), new SafeTokens(), SuzTransportTests.OptionsFor());
            var safeExecutor = new ExecuteApiOperationService(new ApiOperationRegistry([new SuzDocumentStatusHandler(failingTransport)]), profiles, history, new SensitiveDataMasker());
            var failed = await safeExecutor.ExecuteAsync(SuzDocumentStatusHandler.OperationId, Input(), profile.Id, Ct);
            Assert.False(failed.Success);
            Assert.DoesNotContain("sentinel-private", failed.Error!.Message);
            var failedRow = (await history.ListAsync(profile.Id, 100, Ct)).Single(x => x.CorrelationId == failed.CorrelationId);
            Assert.DoesNotContain("sentinel-private", failedRow.Error!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static async Task<JsonNode> Sample()
    {
        var exchange = await new SuzDocumentStatusHandler(new MockSuzClient()).ExecuteAsync(Input(), Context, Ct);
        return JsonNode.Parse(exchange.ResponseBody!)!;
    }
    private sealed class CaptureClient : ISuzClient
    {
        public IntegrationRequest? Request { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(new IntegrationExchange(200, request.Method, request.Url, request.Headers, null, new Dictionary<string, string>(), "{}", TimeSpan.FromMilliseconds(1)));
        }
    }
    private sealed class DbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    {
        public WorkbenchDbContext CreateDbContext() => new(options);
    }
    private sealed class SafeTokens : ISuzTokenProvider
    {
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext a, SuzConnectionContext c, string id, CancellationToken ct = default) => Task.FromResult("private-token");
        public void Invalidate(IntegrationAuthenticationContext a, SuzConnectionContext c, string token) { }
    }
}
