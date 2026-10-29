using System.Net;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class PackageTransformationTests
{
    private const string Item = "010460000000000021DEMO000000001";
    private const string Parent = "000000000000000001";

    [Fact]
    public void ParserPreservesPairsAndReportsLineErrors()
    {
        var parsed = PackageTransformationService.Parse($"{Item}\t{Parent}\n{Item}\t{Parent}\nwrong");
        Assert.Single(parsed.Rows);
        Assert.Equal(1, parsed.PackageCount);
        Assert.Contains(parsed.Errors, x => x.Contains("Строка 2", StringComparison.Ordinal));
        Assert.Contains(parsed.Errors, x => x.Contains("Строка 3", StringComparison.Ordinal));
    }

    [Fact]
    public void ParserAcceptsSpacesAndTabsFromPlainText()
    {
        var secondItem = "046000000000000001";
        var parsed = PackageTransformationService.Parse($"{Item} {Parent}\n{secondItem}   \t {Parent}");
        Assert.Empty(parsed.Errors);
        Assert.Equal([Item, secondItem], parsed.Rows.Select(x => x.Item));
        Assert.Equal(1, parsed.PackageCount);
    }

    [Fact]
    public void ParserRemovesGroupSeparatorSuffixFromBothCodes()
    {
        var parsed = PackageTransformationService.Parse($"{Item}\u001D91ITEM\t{Parent}\u001D91PARENT");
        Assert.Empty(parsed.Errors);
        var row = Assert.Single(parsed.Rows);
        Assert.Equal(Item, row.Item);
        Assert.Equal(Parent, row.Package);
    }

    [Fact]
    public async Task OneParentCreatesSignedDocumentAndDurableGuid()
    {
        var repository = new MemoryRepository();
        var profile = new CrptProfile { Inn = "7700000000", CertificateThumbprint = new string('A', 40) };
        var client = new RecordingClient(false);
        var signer = new RecordingSigner();
        var service = CreateService(repository, new OneProfile(profile), client, signer, TimeProvider.System);
        var sent = await service.SubmitAsync(profile.Id, "milk", "ADDING",
            [new(1, Item, Parent), new(2, "046000000000000001", Parent, true)], TestContext.Current.CancellationToken);
        Assert.Single(sent);
        Assert.Equal("Tracking", sent[0].State);
        Assert.NotNull(sent[0].DocumentId);
        Assert.Equal("Prepared", repository.States[0]);
        Assert.Contains("Sending", repository.States);
        using var outer = JsonDocument.Parse(client.LastRequest!.Body!);
        Assert.Equal("REAGGREGATION_DOCUMENT", outer.RootElement.GetProperty("type").GetString());
        Assert.Equal("MANUAL", outer.RootElement.GetProperty("document_format").GetString());
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(outer.RootElement.GetProperty("product_document").GetString()!));
        Assert.Equal(raw, signer.SignedText);
        using var inner = JsonDocument.Parse(raw);
        Assert.Equal("ADDING", inner.RootElement.GetProperty("reaggregation_type").GetString());
        Assert.Equal(Parent, inner.RootElement.GetProperty("uitu").GetString());
        var children = inner.RootElement.GetProperty("uit_uitu_list");
        Assert.True(children[0].TryGetProperty("uit_uitu", out _));
        Assert.False(children[0].TryGetProperty("kitu", out _));
        Assert.True(children[1].TryGetProperty("kitu", out _));
        Assert.False(children[1].TryGetProperty("uit_uitu", out _));
    }

    [Fact]
    public async Task LostCreateResponseIsNeverRetried()
    {
        var repository = new MemoryRepository();
        var profile = new CrptProfile { Inn = "7700000000" };
        var client = new RecordingClient(true) { FailCreation = true };
        var service = CreateService(repository, new OneProfile(profile), client, new RecordingSigner(), TimeProvider.System);
        var sent = await service.SubmitAsync(profile.Id, "milk", "REMOVING", [new(1, Item, Parent)], TestContext.Current.CancellationToken);
        Assert.Equal("Unknown", sent[0].State);
        Assert.Single(client.Requests);
        var tracker = new PackageDocumentTracker(repository, new OneProfile(profile), client, TimeProvider.System, History(), new SensitiveDataMasker());
        await tracker.PollDueAsync(TestContext.Current.CancellationToken);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/lk/documents/create?pg=milk", true)]
    [InlineData("GET", "https://markirovka.crpt.ru/api/v3/true-api/lk/documents/create?pg=milk", false)]
    [InlineData("POST", "https://markirovka.crpt.ru/api/v3/true-api/lk/documents/create?pg=milk&other=1", false)]
    [InlineData("POST", "https://other.example/api/v3/true-api/lk/documents/create?pg=milk", false)]
    public async Task ProductionGuardLimitsCreation(string method, string url, bool allowed)
    {
        using var guard = new ProductionTrueApiNetworkGuardHandler { InnerHandler = new OkHandler() };
        using var client = new HttpMessageInvoker(guard);
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (allowed) Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TrackingPersistsTerminalStatusAndStopsPolling()
    {
        var repository = new MemoryRepository();
        var profile = new CrptProfile { Inn = "7700000000" };
        var client = new RecordingClient(true);
        var sent = await CreateService(repository, new OneProfile(profile), client,
            new RecordingSigner(), TimeProvider.System).SubmitAsync(profile.Id, "milk", "ADDING", [new(1, Item, Parent)], TestContext.Current.CancellationToken);
        sent[0].NextCheckAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        await new PackageDocumentTracker(repository, new OneProfile(profile), client, TimeProvider.System, History(), new SensitiveDataMasker())
            .PollDueAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Finished", sent[0].State);
        Assert.Equal("CHECKED_OK", sent[0].RemoteStatus);
        Assert.Null(sent[0].NextCheckAtUnixMs);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task CheckNowPreservesLiveLeaseAndMakesDocumentImmediatelyDue()
    {
        var repository = new MemoryRepository();
        var profile = new CrptProfile { Inn = "7700000000" };
        var service = CreateService(repository, new OneProfile(profile), new RecordingClient(true),
            new RecordingSigner(), TimeProvider.System);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var document = new PackageDocument
        {
            ProfileId = profile.Id, ProductGroup = "milk", Action = "ADDING", PackageCode = Parent,
            DocumentHash = new string('A', 64), State = "Tracking", DocumentId = Guid.NewGuid().ToString(),
            NextCheckAtUnixMs = now + 300_000, TrackingUntilUnixMs = now + 86_400_000, LeaseUntilUnixMs = now + 120_000
        };
        await repository.AddAsync(document, TestContext.Current.CancellationToken);

        await service.CheckNowAsync(profile.Id, document.Id, TestContext.Current.CancellationToken);

        Assert.Equal(now + 120_000, document.LeaseUntilUnixMs);
        Assert.True(document.NextCheckAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task PendingGuidSurvivesRepositoryRecreationAndLeaseRecovers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = $"Data Source=file:transform-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(connectionString).Options;
        var factory = new PooledDbContextFactory<WorkbenchDbContext>(options);
        await DatabaseInitializer.InitializeAsync(factory, cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var document = new PackageDocument
        {
            ProfileId = Guid.NewGuid(), ProductGroup = "milk", Action = "ADDING", PackageCode = Parent,
            DocumentHash = new string('A', 64), State = "Tracking", DocumentId = Guid.NewGuid().ToString(),
            NextCheckAtUnixMs = now - 1, TrackingUntilUnixMs = now + 86_400_000
        };
        await new PackageDocumentRepository(factory).AddAsync(document, cancellationToken);
        var reopened = new PackageDocumentRepository(factory);
        Assert.Equal(document.DocumentId, (await reopened.GetAsync(document.Id, cancellationToken))?.DocumentId);
        Assert.Single(await reopened.ClaimDueAsync(now, 10, cancellationToken));
        Assert.Empty(await reopened.ClaimDueAsync(now, 10, cancellationToken));
        Assert.Single(await reopened.ClaimDueAsync(now + 120_001, 10, cancellationToken));
    }

    private static PackageTransformationService CreateService(IPackageDocumentRepository repository, IProfileRepository profiles,
        ITrueApiClient client, ICertificateSignatureService signer, TimeProvider clock) =>
        new(new PackageDocumentService(repository, profiles, client, signer, clock, History()));

    private static PackageDocumentHistory History() => new(new MemoryLogs(), new SensitiveDataMasker(), TimeProvider.System);
    private sealed class MemoryLogs : IApiRequestLogRepository
    {
        public Task AddAsync(ApiRequestLog entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ApiRequestLog>> ListAsync(Guid profileId, int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ApiRequestLog>>([]);
        public Task<ApiRequestLog?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ApiRequestLog?>(null);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class MemoryRepository : IPackageDocumentRepository
    {
        public Task<bool> CompleteCheckAsync(PackageDocument document, long leaseUntilUnixMs, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ScheduleAsync(Guid profileId, Guid id, PackageOperationKind kind, long nowUnixMs, bool resume, CancellationToken cancellationToken = default)
        {
            var record = Items.SingleOrDefault(x => x.Id == id && x.ProfileId == profileId && x.OperationKind == kind &&
                x.DocumentId != null && x.State == (resume ? "Paused" : "Tracking"));
            if (record is null) return Task.FromResult(false);
            record.NextCheckAtUnixMs = nowUnixMs;
            if (resume) { record.State = "Tracking"; record.TrackingUntilUnixMs = nowUnixMs + 86_400_000; record.LeaseUntilUnixMs = null; }
            return Task.FromResult(true);
        }
        public List<PackageDocument> Items { get; } = [];
        public List<string> States { get; } = [];
        public Task AddAsync(PackageDocument document, CancellationToken cancellationToken = default) { Items.Add(document); States.Add(document.State); return Task.CompletedTask; }
        public Task UpdateAsync(PackageDocument document, CancellationToken cancellationToken = default) { States.Add(document.State); return Task.CompletedTask; }
        public Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, PackageOperationKind kind, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageDocument>>(Items);
        public Task<PackageDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<PackageDocument>> ClaimDueAsync(long nowUnixMs, int take, CancellationToken cancellationToken = default)
        {
            var due = Items.Where(x => x.State == "Tracking" && x.NextCheckAtUnixMs <= nowUnixMs).Take(take).ToArray();
            foreach (var item in due) item.LeaseUntilUnixMs = nowUnixMs + 120_000;
            return Task.FromResult<IReadOnlyList<PackageDocument>>(due);
        }
    }
    private sealed class OneProfile(CrptProfile profile) : IProfileRepository
    {
        public Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CrptProfile>>([profile]);
        public Task<CrptProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<CrptProfile?>(id == profile.Id ? profile : null);
        public Task UpsertAsync(CrptProfile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class RecordingSigner : ICertificateSignatureService
    {
        public string? SignedText { get; private set; }
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SignDetachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default)
        { SignedText = data; return Task.FromResult("detached-signature"); }
    }
    private sealed class RecordingClient(bool mock) : ITrueApiClient
    {
        public bool FailCreation { get; set; }
        public Uri BaseAddress { get; } = new(mock ? "https://trueapi.mock.invalid" : "https://markirovka.crpt.ru/api/v3/true-api/");
        public List<IntegrationRequest> Requests { get; } = [];
        public IntegrationRequest? LastRequest => Requests.LastOrDefault();
        public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.BeforeDispatchAsync is not null) await request.BeforeDispatchAsync(cancellationToken);
            Requests.Add(request);
            if (FailCreation) throw new HttpRequestException("connection lost");
            var isStatus = request.Method == "GET";
            var body = isStatus ? "[{\"status\":\"CHECKED_OK\",\"errors\":[]}]" : JsonSerializer.Serialize(Guid.NewGuid().ToString());
            return new IntegrationExchange(isStatus ? 200 : 201, request.Method, request.Url, request.Headers, request.Body,
                new Dictionary<string, string>(), body, TimeSpan.Zero);
        }
    }
}
