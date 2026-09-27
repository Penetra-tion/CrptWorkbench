using System.Net;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Clients;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class CodeCorrectionTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Code = "010460000000000021CORRECT000001";
    private static IntegrationRequest Request(string group = "milk", string field = "productionDate", string type = "CIS_INFORMATION_CHANGE",
        string operation = "true-api.code-correction")
    {
        var document = JsonSerializer.Serialize(new
        {
            participantInn = "7700000000",
            codes = new[] { new Dictionary<string, object> { ["code"] = new[] { Code }, [field] = "2026-09-01" } }
        });
        return new(operation, "POST", $"lk/documents/create?pg={group}", new Dictionary<string, string>(),
            JsonSerializer.Serialize(new { type, document_format = "MANUAL", product_document = Convert.ToBase64String(Encoding.UTF8.GetBytes(document)), signature = "private-signature" }),
            "correction-correlation", new(Guid.NewGuid(), "7700000000", new string('A', 40)));
    }

    [Fact]
    public async Task CorrectionPreservesEnvelopeAndUsesApprovedCreationEndpoint()
    {
        using var factory = new Factory();
        var tokens = new Tokens();
        var request = Request();
        var response = await Client(factory, tokens).ExecuteAsync(request, Ct);
        Assert.Equal(201, response.StatusCode);
        Assert.Equal(request.Body, factory.Body);
        Assert.Equal($"{CrptIntegrationOptions.ProductionTrueApiBaseUrl}/lk/documents/create?pg=milk", factory.Url);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, tokens.Calls);
    }

    [Fact]
    public async Task InvalidCorrectionCombinationsFailBeforeAuthentication()
    {
        IntegrationRequest[] invalid =
        [
            Request(group: "unknown"), Request(group: "milk&pg=shoes"), Request(group: "milk#fragment"),
            Request(group: "shoes", field: "weight"), Request(field: "unknownField"),
            Request(type: "AGGREGATION_DOCUMENT"), Request(type: "REAGGREGATION_DOCUMENT"),
            Request(operation: "true-api.package-aggregation"), Request(operation: "true-api.package-transformation"),
            Request(group: "tobacco", operation: "true-api.package-aggregation", type: "AGGREGATION_DOCUMENT"),
            Request(group: "tobacco", operation: "true-api.package-transformation", type: "REAGGREGATION_DOCUMENT"),
            Request() with { Method = "GET" }, Request() with { Body = "{}" }
        ];
        foreach (var request in invalid)
        {
            using var factory = new Factory();
            var tokens = new Tokens();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Client(factory, tokens).ExecuteAsync(request, Ct));
            Assert.Equal(0, tokens.Calls);
            Assert.Equal(0, factory.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainCorrectionIsNeverRetried(bool timeout)
    {
        using var factory = new Factory { Timeout = timeout, Status = HttpStatusCode.ServiceUnavailable };
        var tokens = new Tokens();
        if (timeout) await Assert.ThrowsAsync<TaskCanceledException>(() => Client(factory, tokens).ExecuteAsync(Request(), Ct));
        else Assert.Equal(503, (await Client(factory, tokens).ExecuteAsync(Request(), Ct)).StatusCode);
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, tokens.Calls);
    }

    [Fact]
    public async Task EverySupportedGroupKeepsCorrectionMetadataAfterMockRestart()
    {
        foreach (var group in CodeCorrectionRules.Groups)
        {
            var response = await new MockTrueApiClient().ExecuteAsync(Request(group.Code), Ct);
            var id = JsonSerializer.Deserialize<string>(response.ResponseBody!)!;
            Assert.True(Guid.TryParse(id, out _));
            var polled = await new MockTrueApiClient().ExecuteAsync(new("true-api.document-status", "GET",
                $"doc/{id}/info?body=false&content=false", new Dictionary<string, string>(), null, "status-correlation"), Ct);
            using var json = JsonDocument.Parse(polled.ResponseBody!);
            Assert.Equal("CIS_INFORMATION_CHANGE", json.RootElement[0].GetProperty("type").GetString());
            Assert.Equal(group.Code, json.RootElement[0].GetProperty("productGroup")[0].GetString());
            Assert.Equal("CHECKED_OK", json.RootElement[0].GetProperty("status").GetString());
            Assert.Equal("true", polled.ResponseHeaders["X-CRPT-Workbench-Mock"]);
        }
    }

    [Fact]
    public async Task CorrectionFixturesProvideIndependentGroupStatusAndNoParent()
    {
        var codes = Enumerable.Range(1, 4).Select(i => $"010460000000000021CORRECT{i:000000}").ToArray();
        var request = new IntegrationRequest("true-api.code-info", "POST", "https://production.trueapi.mock.invalid/cises/info",
            new Dictionary<string, string>(), JsonSerializer.Serialize(codes), "fixture-correlation");
        var response = await new MockTrueApiClient().ExecuteAsync(request, Ct);
        var items = JsonSerializer.Deserialize<CodeInfoItem[]>(response.ResponseBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(["INTRODUCED", "RETIRED", "APPLIED", "APPLIED"], items.Select(x => x.CisInfo!.Status));
        Assert.Equal(["milk", "milk", "shoes", "milk"], items.Select(x => x.CisInfo!.ProductGroup));
        Assert.All(items, item => Assert.Null(item.CisInfo!.Parent));
    }

    private static RealTrueApiClient Client(Factory factory, Tokens tokens) => new(factory, tokens,
        Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production, TrueApiBaseUrl = CrptIntegrationOptions.ProductionTrueApiBaseUrl }));

    [Fact]
    public async Task CorrectionTrackingSurvivesRepositoryRecreationAndKeepsMaskedHistory()
    {
        var cs = $"Data Source=file:correction-tracking-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(cs);
        await anchor.OpenAsync(Ct);
        var factory = new PooledDbContextFactory<WorkbenchDbContext>(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(cs).Options);
        await DatabaseInitializer.InitializeAsync(factory, Ct);
        var profile = new CrptProfile { Inn = "7700000000" };
        var response = await new MockTrueApiClient().ExecuteAsync(Request(), Ct);
        var now = DateTimeOffset.UtcNow;
        var document = new PackageDocument
        {
            ProfileId = profile.Id, OperationKind = PackageOperationKind.CodeCorrection, Mode = CrptIntegrationMode.Mock,
            ProductGroup = "milk", Action = "productionDate", PackageCode = string.Empty, ItemCount = 1,
            State = "Tracking", DocumentId = JsonSerializer.Deserialize<string>(response.ResponseBody!),
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(), SubmittedAtUnixMs = now.ToUnixTimeMilliseconds(),
            NextCheckAtUnixMs = now.ToUnixTimeMilliseconds(), TrackingUntilUnixMs = now.AddHours(24).ToUnixTimeMilliseconds()
        };
        await new PackageDocumentRepository(factory).AddAsync(document, Ct);
        var reopened = new PackageDocumentRepository(factory);
        Assert.Single(await reopened.ListAsync(profile.Id, PackageOperationKind.CodeCorrection, Ct));
        Assert.Empty(await reopened.ListAsync(profile.Id, PackageOperationKind.Aggregation, Ct));
        Assert.Empty(await reopened.ListAsync(profile.Id, PackageOperationKind.Transformation, Ct));
        var logs = new ApiRequestLogRepository(factory);
        var masker = new SensitiveDataMasker();
        var tracker = new PackageDocumentTracker(reopened, new Profiles(profile), new MockTrueApiClient(), TimeProvider.System,
            new PackageDocumentHistory(logs, masker, TimeProvider.System), masker);
        await tracker.PollDueAsync(Ct);
        var saved = (await new PackageDocumentRepository(factory).GetAsync(document.Id, Ct))!;
        Assert.Equal("Finished", saved.State);
        Assert.Equal("CHECKED_OK", saved.RemoteStatus);
        Assert.Equal(document.DocumentId, saved.DocumentId);
        Assert.Null(saved.NextCheckAtUnixMs);
        var history = Assert.Single(await logs.ListAsync(10, Ct));
        Assert.Contains("Bearer ********", history.RequestHeaders);
        Assert.DoesNotContain("mock-token-not-a-real-secret", history.RequestHeaders);
        Assert.Contains("X-CRPT-Workbench-Mock", history.ResponseHeaders);
    }

    private sealed class Profiles(CrptProfile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CrptProfile>>([profile]);
        public Task<CrptProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<CrptProfile?>(id == profile.Id ? profile : null);
        public Task UpsertAsync(CrptProfile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Tokens : ITrueApiTokenProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext authentication, string correlationId, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult("private-token"); }
        public void Invalidate(IntegrationAuthenticationContext authentication) { }
    }

    private sealed class Factory : IHttpClientFactory, IDisposable
    {
        private HttpClient? _client;
        public int Calls { get; set; }
        public string? Body { get; set; }
        public string? Url { get; set; }
        public bool Timeout { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.Created;
        public HttpClient CreateClient(string name) => _client ??= new HttpClient(new Handler(this)) { BaseAddress = new(CrptIntegrationOptions.ProductionTrueApiBaseUrl + "/") };
        public void Dispose() => _client?.Dispose();
        private sealed class Handler(Factory factory) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                factory.Calls++;
                factory.Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                factory.Url = request.RequestUri!.AbsoluteUri;
                if (factory.Timeout) throw new TaskCanceledException("Simulated timeout");
                return new(factory.Status) { Content = new StringContent(JsonSerializer.Serialize(Guid.NewGuid().ToString())) };
            }
        }
    }
}
