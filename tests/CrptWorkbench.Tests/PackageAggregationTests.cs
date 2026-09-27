using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class PackageAggregationTests
{
    private const string Item = "010460000000000021DEMO000000001";
    private const string SecondItem = "010460000000000021DEMO000000002";
    private const string Parent = "000000000000000001";
    private const string SecondParent = "000000000000000002";

    [Theory]
    [InlineData(18)]
    [InlineData(74)]
    public void ParserAcceptsBoundaryLengthsWithoutControlDigitChecks(int length)
    {
        var preview = PackageAggregationService.Parse(new string('0', length) + "\t" + Parent);
        Assert.Empty(preview.Errors);
        Assert.Single(preview.Rows);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(75)]
    public void ParserRejectsOutsideLengthBoundaries(int length)
    {
        Assert.NotEmpty(PackageAggregationService.Parse(new string('0', length) + "\t" + Parent).Errors);
        Assert.NotEmpty(PackageAggregationService.Parse(Item + "\t" + new string('0', length)).Errors);
    }

    [Theory]
    [InlineData("010460000000000021DEMO#00000001")]
    [InlineData("010460000000000021ДЕМО00000001")]
    [InlineData("010460000000000021DEMO\u001d000001")]
    public void ParserRejectsDisallowedCharacters(string code)
    {
        Assert.NotEmpty(PackageAggregationService.Parse(code + "\t" + Parent).Errors);
        Assert.NotEmpty(PackageAggregationService.Parse(Item + "\t" + code).Errors);
    }

    [Fact]
    public void ParserPreservesGroupingOrderAndLineNumbers()
    {
        var preview = PackageAggregationService.Parse($"\r\n{Item}\t{Parent}\r\n{SecondItem} {SecondParent}\r\n046000000000000001   {Parent}");
        Assert.Empty(preview.Errors);
        Assert.Equal(2, preview.PackageCount);
        Assert.Equal([2, 3, 4], preview.Rows.Select(x => x.Line));
        Assert.Equal([Parent, SecondParent, Parent], preview.Rows.Select(x => x.Package));
    }

    [Theory]
    [InlineData("0204600000000000371")]
    [InlineData("020460000000000037123456783103123456789")]
    public async Task ForbiddenAggregateMasksCannotBeSubmitted(string package)
    {
        var fixture = new Fixture();
        Assert.Empty(PackageAggregationService.Parse(Item + "\t" + package, createSet: true).Errors);
        Assert.Contains(PackageAggregationService.Parse(Item + "\t" + package).Errors,
            error => error.Contains("маске", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Submit([new(1, Item, package)]));
        fixture.AssertNoSideEffects();
    }

    [Fact]
    public async Task DuplicateSelfAndDependentPackagesAreRejectedWithoutSideEffects()
    {
        IReadOnlyList<TransformationRow>[] invalid =
        [
            [new(1, Item, Parent), new(2, Item, Parent)],
            [new(1, Item, Parent), new(2, Item, SecondParent)],
            [new(1, Parent, Parent)],
            [new(1, Item, Parent), new(2, Parent, SecondParent)],
            [new(1, "short", Parent)],
            [new(1, " " + Item, Parent)],
            []
        ];
        foreach (var rows in invalid)
        {
            var fixture = new Fixture();
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Submit(rows));
            fixture.AssertNoSideEffects();
        }
        var preview = PackageAggregationService.Parse($"{Item}\t{Parent}\n{Parent}\t{SecondParent}");
        Assert.Contains(preview.Errors, error => error.Contains("нижний уровень", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LargeInputAboveTwentyThousandRowsAndOneMegabyteCanBeSubmitted()
    {
        var lines = Enumerable.Range(1, 20001).Select(i => "010460000000000021" + i.ToString("D30") + "\t999999999999999998");
        var text = string.Join('\n', lines);
        Assert.True(Encoding.UTF8.GetByteCount(text) > 1_000_000);
        var preview = PackageAggregationService.Parse(text);
        Assert.Empty(preview.Errors);
        Assert.Equal(20001, preview.Rows.Count);
        var fixture = new Fixture();
        var sent = Assert.Single(await fixture.Submit(preview.Rows));
        Assert.Equal(20001, sent.ItemCount);
        Assert.Equal("Tracking", sent.State);
        using var payload = JsonDocument.Parse(Assert.Single(fixture.Signer.Signed));
        Assert.Equal(20001, payload.RootElement.GetProperty("aggregationUnits")[0].GetProperty("sntins").GetArrayLength());
        Assert.NotEmpty(PackageTransformationService.Parse(text).Errors);
    }

    [Fact]
    public async Task UnsupportedSetGroupHasNoSideEffects()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SubmitAsync(fixture.Profile.Id,
            "shoes", "SET", [new(1, Item, Parent)], TestContext.Current.CancellationToken));
        fixture.AssertNoSideEffects();
    }

    [Theory]
    [InlineData("milk", "UNIT")]
    [InlineData("milk", "box")]
    [InlineData("unknown", "BOX")]
    public async Task InvalidSelectorsCannotCreateRecordsOrCallSigning(string group, string packageType)
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SubmitAsync(fixture.Profile.Id,
            group, packageType, [new(1, Item, Parent)], TestContext.Current.CancellationToken));
        fixture.AssertNoSideEffects();
    }

    [Theory]
    [InlineData("BOX")]
    [InlineData("GROUP")]
    [InlineData("AUTO")]
    [InlineData("SET")]
    public async Task EachPackageHasExactSignedContractAndItsOwnGuid(string packageType)
    {
        var fixture = new Fixture();
        var documents = await fixture.Service.SubmitAsync(fixture.Profile.Id, "milk", packageType,
            [new(1, Item, Parent), new(2, SecondItem, SecondParent), new(3, "046000000000000001", Parent)],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, documents.Count);
        Assert.Equal(2, documents.Select(x => x.DocumentId).Distinct().Count());
        Assert.Equal([2, 1], documents.Select(x => x.ItemCount));
        Assert.Equal(["Prepared", "Sending", "Tracking", "Prepared", "Sending", "Tracking"], fixture.Repository.States);
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            var request = fixture.Client.Requests[i];
            Assert.Equal(PackageOperationKind.Aggregation, document.OperationKind);
            Assert.Equal(packageType, document.PackageType);
            Assert.Equal("Tracking", document.State);
            Assert.True(Guid.TryParse(document.DocumentId, out _));
            Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(5).ToUnixTimeMilliseconds(), document.NextCheckAtUnixMs);
            Assert.Equal(fixture.Clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds(), document.TrackingUntilUnixMs);
            Assert.Equal("true-api.package-aggregation", request.OperationId);
            Assert.Equal("POST", request.Method);
            Assert.Equal("lk/documents/create?pg=milk", request.Url);
            Assert.Equal(fixture.Profile.Id, request.Authentication!.ProfileId);
            using var envelope = JsonDocument.Parse(request.Body!);
            Assert.Equal(packageType == "SET" ? "SETS_AGGREGATION" : "AGGREGATION_DOCUMENT", envelope.RootElement.GetProperty("type").GetString());
            Assert.Equal("MANUAL", envelope.RootElement.GetProperty("document_format").GetString());
            Assert.Equal("sensitive-signature", envelope.RootElement.GetProperty("signature").GetString());
            var bytes = Convert.FromBase64String(envelope.RootElement.GetProperty("product_document").GetString()!);
            Assert.Equal(Encoding.UTF8.GetBytes(fixture.Signer.Signed[i]), bytes);
            var expectedItems = i == 0 ? $"\"{Item}\",\"046000000000000001\"" : $"\"{SecondItem}\"";
            var expected = "{\"participantId\":\"7700000000\",\"aggregationUnits\":[{\"unitSerialNumber\":\"" +
                document.PackageCode + "\"," + (packageType == "SET" ? "" : "\"aggregationType\":\"AGGREGATION\",") + "\"sntins\":[" + expectedItems + "]}]}";
            Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public async Task MockSubmissionNeverInvokesCertificateSigning()
    {
        var fixture = new Fixture(mock: true);
        var sent = await fixture.Submit([new(1, Item, Parent)]);
        Assert.Equal(CrptIntegrationMode.Mock, Assert.Single(sent).Mode);
        Assert.Empty(fixture.Signer.Signed);
        Assert.Contains("X-CRPT-Workbench-Mock", Assert.Single(fixture.Logs.Items).ResponseHeaders!);
    }

    [Theory]
    [InlineData(200, "{}", "Unknown")]
    [InlineData(201, "not-a-guid", "Unknown")]
    [InlineData(204, "", "Unknown")]
    [InlineData(408, "{}", "Unknown")]
    [InlineData(500, "{}", "Unknown")]
    [InlineData(503, "{}", "Unknown")]
    [InlineData(400, "{}", "Rejected")]
    [InlineData(403, "{}", "Rejected")]
    public async Task AmbiguousResponsesAreNotResentAndClientErrorsAreRejected(int status, string body, string expected)
    {
        var fixture = new Fixture();
        fixture.Client.Status = status;
        fixture.Client.ResponseBody = body;
        var document = Assert.Single(await fixture.Submit([new(1, Item, Parent)]));
        Assert.Equal(expected, document.State);
        Assert.Null(document.DocumentId);
        Assert.Null(document.NextCheckAtUnixMs);
        Assert.Single(fixture.Client.Requests);
        Assert.False(Assert.Single(fixture.Logs.Items).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionLossAndTimeoutAreUncertainWithoutRetry(bool timeout)
    {
        var fixture = new Fixture();
        fixture.Client.Failure = timeout ? new OperationCanceledException("timeout") : new HttpRequestException("lost response");
        var document = Assert.Single(await fixture.Submit([new(1, Item, Parent)]));
        Assert.Equal("Unknown", document.State);
        Assert.Single(fixture.Client.Requests);
        Assert.Null(document.DocumentId);
    }

    [Fact]
    public async Task CancellationAfterReceivingGuidStillPersistsGuidAndHistory()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var fixture = new Fixture();
        fixture.Client.BeforeReturning = cancellation.Cancel;
        var document = Assert.Single(await fixture.Service.SubmitAsync(fixture.Profile.Id, "milk", "BOX",
            [new(1, Item, Parent)], cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("Tracking", document.State);
        Assert.Equal(document.DocumentId, fixture.Repository.PersistedGuid);
        Assert.False(fixture.Repository.LastUpdateToken.CanBeCanceled);
        Assert.Single(fixture.Logs.Items);
    }

    [Fact]
    public async Task CancellationDuringSendingLeavesDurableUnknownWithoutRetry()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var fixture = new Fixture();
        fixture.Client.BeforeReturning = () =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SubmitAsync(fixture.Profile.Id,
            "milk", "BOX", [new(1, Item, Parent)], cancellation.Token));
        Assert.Equal("Unknown", Assert.Single(fixture.Repository.Items).State);
        Assert.Equal("Unknown", fixture.Repository.States.Last());
        Assert.False(fixture.Repository.LastUpdateToken.CanBeCanceled);
        Assert.Single(fixture.Client.Requests);
        Assert.False(Assert.Single(fixture.Logs.Items).Success);
    }

    [Fact]
    public async Task HistoryMasksAuthorizationSignatureAndNestedSecretsAndPreservesCorrelation()
    {
        var fixture = new Fixture();
        fixture.Client.Status = 400;
        fixture.Client.ResponseBody = "{\"errors\":[{\"token\":\"response-secret\"}]}";
        await fixture.Submit([new(1, Item, Parent)]);
        var request = Assert.Single(fixture.Client.Requests);
        var log = Assert.Single(fixture.Logs.Items);
        Assert.Equal(request.CorrelationId, request.Headers["X-Correlation-Id"]);
        Assert.Equal(request.CorrelationId, log.CorrelationId);
        Assert.Equal(request.OperationId, log.OperationId);
        Assert.Equal(fixture.Profile.Id, log.ProfileId);
        Assert.True(log.DurationMs >= 0);
        Assert.Contains("Bearer ********", log.RequestHeaders!);
        Assert.DoesNotContain("auth-secret", log.RequestHeaders!);
        Assert.DoesNotContain("sensitive-signature", log.RequestBody!);
        Assert.DoesNotContain("response-secret", log.ResponseBody!);
        using var masked = JsonDocument.Parse(log.RequestBody!);
        Assert.Equal("********", masked.RootElement.GetProperty("signature").GetString());
        Assert.Equal(400, log.StatusCode);
    }

    private sealed class Fixture
    {
        public CrptProfile Profile { get; } = new() { Inn = "7700000000", CertificateThumbprint = new string('A', 40) };
        public MemoryRepository Repository { get; } = new();
        public RecordingSigner Signer { get; } = new();
        public MemoryLogs Logs { get; } = new();
        public FixedClock Clock { get; } = new();
        public RecordingClient Client { get; }
        public PackageAggregationService Service { get; }
        public Fixture(bool mock = false)
        {
            Client = new RecordingClient(mock);
            Service = new PackageAggregationService(new PackageDocumentService(Repository, new OneProfile(Profile), Client,
                Signer, Clock, new PackageDocumentHistory(Logs, new SensitiveDataMasker(), Clock)));
        }
        public Task<IReadOnlyList<PackageDocument>> Submit(IReadOnlyList<TransformationRow> rows) =>
            Service.SubmitAsync(Profile.Id, "milk", "BOX", rows, TestContext.Current.CancellationToken);
        public void AssertNoSideEffects()
        {
            Assert.Empty(Repository.Items);
            Assert.Empty(Client.Requests);
            Assert.Empty(Signer.Signed);
            Assert.Empty(Logs.Items);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class MemoryRepository : IPackageDocumentRepository
    {
        public List<PackageDocument> Items { get; } = [];
        public List<string> States { get; } = [];
        public string? PersistedGuid { get; private set; }
        public CancellationToken LastUpdateToken { get; private set; }
        public Task AddAsync(PackageDocument document, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Items.Add(document); States.Add(document.State); return Task.CompletedTask; }
        public Task UpdateAsync(PackageDocument document, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUpdateToken = cancellationToken;
            PersistedGuid = document.DocumentId;
            States.Add(document.State);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, PackageOperationKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageDocument>>(Items.Where(x => x.ProfileId == profileId && x.OperationKind == kind).ToArray());
        public Task<PackageDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task<bool> ScheduleAsync(Guid profileId, Guid id, PackageOperationKind kind, long nowUnixMs, bool resume, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> CompleteCheckAsync(PackageDocument document, long leaseUntilUnixMs, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PackageDocument>> ClaimDueAsync(long nowUnixMs, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageDocument>>(Items.Where(x => x.State == "Tracking" && x.NextCheckAtUnixMs <= nowUnixMs).Take(take).ToArray());
    }

    private sealed class OneProfile(CrptProfile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CrptProfile>>([profile]);
        public Task<CrptProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<CrptProfile?>(id == profile.Id ? profile : null);
        public Task UpsertAsync(CrptProfile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingSigner : ICertificateSignatureService
    {
        public List<string> Signed { get; } = [];
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SignDetachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default)
        { Signed.Add(data); return Task.FromResult("sensitive-signature"); }
    }

    private sealed class MemoryLogs : IApiRequestLogRepository
    {
        public List<ApiRequestLog> Items { get; } = [];
        public Task AddAsync(ApiRequestLog entry, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Items.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<ApiRequestLog>> ListAsync(int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ApiRequestLog>>(Items.Take(take).ToArray());
        public Task<ApiRequestLog?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
    }

    private sealed class RecordingClient(bool mock) : ITrueApiClient
    {
        public Uri BaseAddress { get; } = new(mock ? "https://trueapi.mock.invalid" : "https://markirovka.crpt.ru/api/v3/true-api/");
        public List<IntegrationRequest> Requests { get; } = [];
        public int Status { get; set; } = 201;
        public string? ResponseBody { get; set; }
        public Exception? Failure { get; set; }
        public Action? BeforeReturning { get; set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Failure is not null) throw Failure;
            var headers = new Dictionary<string, string>(request.Headers) { ["Authorization"] = "Bearer auth-secret" };
            var exchange = new IntegrationExchange(Status, request.Method, request.Url, headers, request.Body,
                mock ? new Dictionary<string, string> { ["X-CRPT-Workbench-Mock"] = "true" } : new Dictionary<string, string>(),
                ResponseBody ?? JsonSerializer.Serialize(Guid.NewGuid().ToString()), TimeSpan.FromMilliseconds(1));
            BeforeReturning?.Invoke();
            return Task.FromResult(exchange);
        }
    }
}
