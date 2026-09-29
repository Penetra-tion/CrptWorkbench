using System.Text;
using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class CodeCorrectionServiceTests
{
    private const string Good = "010460000000000021CORRECT000001";
    private const string Retired = "010460000000000021CORRECT000002";
    private const string Shoes = "010460000000000021CORRECT000003";
    private const string Other = "010460000000000021CORRECT000004";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CodeCorrectionRequest Request(params string[] codes) => new("milk", "productionDate", new("2026-09-01"), codes.Length == 0 ? [Good] : codes);

    [Fact]
    public void ParserRemovesGroupSeparatorSuffixBeforeDeduplication()
    {
        var parsed = CodeCorrectionService.ParseCodes($"{Good}\u001D91FIRST\n{Good}\u001D91SECOND\n{Retired}");
        Assert.Equal([Good, Retired], parsed.Codes);
        Assert.Equal(1, parsed.DuplicateCount);
    }

    [Theory]
    [InlineData("vsd")]
    [InlineData("productionDate")]
    [InlineData("expirationDate")]
    [InlineData("variableExpiration")]
    [InlineData("weight")]
    [InlineData("tnved")]
    [InlineData("permitDocs")]
    [InlineData("licences")]
    [InlineData("emissionType")]
    [InlineData("partNumber")]
    public async Task EachKindWritesOnlyItsSelectedContractFields(string kind)
    {
        var f = new Fixture(production: true);
        var values = kind switch
        {
            "vsd" => new CorrectionValues("00000000-0000-0000-0000-000000000001"),
            "weight" => new("123"),
            "tnved" => new("1234567890"),
            "emissionType" => new("2"),
            "partNumber" => new("04600000000000260901PART1"),
            "variableExpiration" => new(Expirations: [new(2, "2026-09-01"), new(4, "2026-09-02")]),
            "permitDocs" => new(Operation: 1, PermitDocuments: [new(2, "RU Д-DEMO.АА00.B.00001/25", "2026-09-01")]),
            "licences" => new(Operation: 0, Licences: [new("МОС06318ВЭ1", "2026-09-01")]),
            _ => new("2026-09-01")
        };
        var request = Request() with { ProductGroup = kind == "licences" ? "water" : "milk", Kind = kind, Values = values };
        await f.Service.SubmitAsync(f.Profile.Id, request, false, null, Ct);
        using var envelope = JsonDocument.Parse(Assert.Single(f.Client.Requests).Body!);
        var bytes = Convert.FromBase64String(envelope.RootElement.GetProperty("product_document").GetString()!);
        Assert.Equal(Encoding.UTF8.GetBytes(Assert.Single(f.Signer.Signed)), bytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(["codes", "participantInn"], root.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("7700000000", root.GetProperty("participantInn").GetString());
        var entry = Assert.Single(root.GetProperty("codes").EnumerateArray());
        Assert.Equal(Good, Assert.Single(entry.GetProperty("code").EnumerateArray()).GetString());
        var expectedFields = kind is "permitDocs" or "licences" ? new[] { "code", kind, kind + "Operation" } : new[] { "code", kind };
        Assert.Equal(expectedFields.OrderBy(x => x, StringComparer.Ordinal), entry.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        var value = entry.GetProperty(kind);
        switch (kind)
        {
            case "weight": case "emissionType":
                Assert.Equal(JsonValueKind.Number, value.ValueKind);
                Assert.Equal(kind == "weight" ? 123 : 2, value.GetInt32());
                break;
            case "variableExpiration":
                Assert.Equal(JsonValueKind.Object, value.ValueKind);
                Assert.Equal(2, value.EnumerateObject().Count());
                Assert.Equal(JsonValueKind.Number, value.GetProperty("2").ValueKind);
                Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), value.GetProperty("2").GetInt64());
                Assert.Equal(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), value.GetProperty("4").GetInt64());
                break;
            case "permitDocs":
                Assert.Equal(1, entry.GetProperty("permitDocsOperation").GetInt32());
                var permit = Assert.Single(value.EnumerateArray());
                Assert.Equal(["permitDocDate", "permitDocNumber", "permitDocType"], permit.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
                Assert.Equal(2, permit.GetProperty("permitDocType").GetInt32());
                Assert.Equal("01.09.2026", permit.GetProperty("permitDocDate").GetString());
                Assert.Equal(values.PermitDocuments![0].Number, permit.GetProperty("permitDocNumber").GetString());
                break;
            case "licences":
                Assert.Equal(0, entry.GetProperty("licencesOperation").GetInt32());
                var licence = Assert.Single(value.EnumerateArray());
                Assert.Equal(["licence_date", "licence_number"], licence.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
                Assert.Equal("01.09.2026", licence.GetProperty("licence_date").GetString());
                Assert.Equal(values.Licences![0].Number, licence.GetProperty("licence_number").GetString());
                break;
            default:
                Assert.Equal(JsonValueKind.String, value.ValueKind);
                Assert.Equal(kind is "productionDate" or "expirationDate" ? "01.09.2026" : values.Value, value.GetString());
                break;
        }
    }

    [Fact]
    public void WeightBoundariesRejectOutsideRangeAndFractionalValues()
    {
        var f = new Fixture();
        foreach (var value in new[] { "1", "999999" })
            Assert.Empty(f.Service.Validate(Request() with { Kind = "weight", Values = new(value) }));
        foreach (var value in new[] { "0", "1000000", "1.5", "-1" })
            Assert.NotEmpty(f.Service.Validate(Request() with { Kind = "weight", Values = new(value) }));
    }

    [Fact]
    public void DatesRespectInclusiveBoundariesAndCalendarValidity()
    {
        var f = new Fixture();
        foreach (var value in new[] { "2006-09-27", "2026-09-28" })
            Assert.Empty(f.Service.Validate(Request() with { Values = new(value) }));
        foreach (var value in new[] { "2006-09-26", "2026-09-29", "2026-02-29" })
            Assert.NotEmpty(f.Service.Validate(Request() with { Values = new(value) }));
        foreach (var value in new[] { "1999-01-01", "2099-12-31" })
            Assert.Empty(f.Service.Validate(Request() with { Kind = "expirationDate", Values = new(value) }));
        foreach (var value in new[] { "1998-12-31", "2100-01-01" })
            Assert.NotEmpty(f.Service.Validate(Request() with { Kind = "expirationDate", Values = new(value) }));
    }

    [Fact]
    public void VariableExpirationRequiresDistinctNonnegativeIndexesAndValidDates()
    {
        var f = new Fixture();
        var request = Request() with { Kind = "variableExpiration", Values = new(Expirations: [new(0, "1999-01-01"), new(1, "2099-12-31")]) };
        Assert.Empty(f.Service.Validate(request));
        IReadOnlyList<CorrectionExpiration>[] invalid =
        [
            [], [new(1, "2026-09-01"), new(1, "2026-09-02")],
            [new(-1, "2026-09-01")], [new(1, "2100-01-01")]
        ];
        foreach (var values in invalid)
            Assert.NotEmpty(f.Service.Validate(request with { Values = new(Expirations: values) }));
    }

    [Fact]
    public async Task UnknownGroupNameCannotFallBackToKnownGroupId()
    {
        var f = new Fixture();
        f.Client.Transform = items => items.Select(x => x with { CisInfo = x.CisInfo! with { ProductGroup = "UNKNOWN", ProductGroupId = 8 } }).ToArray();
        var result = await f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: Ct);
        Assert.False(Assert.Single(result.Rows).Passed);
        Assert.Contains(result.Rows[0].Reasons, x => x.Contains("товарную группу", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SubmitAsync(f.Profile.Id, Request(), true, result.CheckId, Ct));
        Assert.Equal(0, f.Client.Creations);
    }

    [Fact]
    public async Task UnknownParentPackagingBlocksTnvedCorrection()
    {
        const string parent = "000000000000000001";
        var f = new Fixture();
        f.Client.Transform = items => items.Select(x => x with
        {
            CisInfo = x.CisInfo!.Cis == parent
                ? x.CisInfo! with { GeneralPackageType = "UNKNOWN", PackageType = "UNKNOWN" }
                : x.CisInfo! with { Parent = parent }
        }).ToArray();
        var request = Request() with { Kind = "tnved", Values = new("1234567890") };
        var result = await f.Service.CheckAsync(f.Profile.Id, request, cancellationToken: Ct);
        Assert.Equal(2, f.Client.Requests.Count);
        Assert.Equal(parent, Assert.Single(JsonSerializer.Deserialize<string[]>(f.Client.Requests[1].Body!)!));
        Assert.False(Assert.Single(result.Rows).Passed);
        Assert.Contains(result.Rows[0].Reasons, x => x.Contains("родительской упаковки", StringComparison.Ordinal));
        Assert.False(f.Service.CanSubmit(f.Profile.Id, request, result.CheckId));
    }

    [Fact]
    public async Task UnknownEmissionBlocksTnvedCorrection()
    {
        var f = new Fixture();
        f.Client.Transform = items => items.Select(x => x with { CisInfo = x.CisInfo! with { EmissionType = "UNKNOWN" } }).ToArray();
        var request = Request() with { Kind = "tnved", Values = new("1234567890") };
        var result = await f.Service.CheckAsync(f.Profile.Id, request, cancellationToken: Ct);
        Assert.False(Assert.Single(result.Rows).Passed);
        Assert.Contains(result.Rows[0].Reasons, x => x.Contains("эмиссии", StringComparison.Ordinal));
        Assert.False(f.Service.CanSubmit(f.Profile.Id, request, result.CheckId));
    }

    [Theory]
    [InlineData("vsd", "00000000-0000-0000-0000-000000000001")]
    [InlineData("licences", "МОС06318ВЭ1")]
    [InlineData("partNumber", "04600000000000260901PART1")]
    public async Task TrailingNewlineInStructuredValuesIsRejected(string kind, string value)
    {
        var f = new Fixture();
        var valid = Request() with
        {
            ProductGroup = kind == "licences" ? "water" : "milk", Kind = kind,
            Values = kind == "licences" ? new(Licences: [new(value, "2026-09-01")]) : new(value)
        };
        Assert.Empty(f.Service.Validate(valid));
        var invalid = valid with { Values = kind == "licences" ? new(Licences: [new(value + "\n", "2026-09-01")]) : new(value + "\n") };
        Assert.NotEmpty(f.Service.Validate(invalid));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SubmitAsync(f.Profile.Id, invalid, false, null, Ct));
        Assert.Empty(f.Client.Requests);
        Assert.Empty(f.Repository.Items);
    }

    [Fact]
    public async Task ReorderedRepliesMapByCodeAndMixedRowsHaveReasons()
    {
        var f = new Fixture();
        f.Client.Transform = items => items.Reverse().ToArray();
        var result = await f.Service.CheckAsync(f.Profile.Id, Request(Good, Retired, Shoes), cancellationToken: Ct);
        Assert.Equal([Good, Retired, Shoes], result.Rows.Select(x => x.Code));
        Assert.True(result.Rows[0].Passed);
        Assert.Contains(result.Rows[1].Reasons, x => x.Contains("Статус", StringComparison.Ordinal));
        Assert.Contains(result.Rows[2].Reasons, x => x.Contains("товарная группа", StringComparison.Ordinal));
        Assert.False(f.Service.CanSubmit(f.Profile.Id, Request(Good, Retired, Shoes), result.CheckId));
    }

    [Fact]
    public async Task MissingReplyCannotBecomeSuccessful()
    {
        var f = new Fixture();
        f.Client.Transform = items => items.Where(x => x.CisInfo!.Cis != Other).ToArray();
        var result = await f.Service.CheckAsync(f.Profile.Id, Request(Good, Other), cancellationToken: Ct);
        Assert.True(result.Rows[0].Passed);
        Assert.False(result.Rows[1].Passed);
        Assert.Contains(result.Rows[1].Reasons, x => x.Contains("отсутствует", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateReplyCannotBecomeSuccessful()
    {
        var f = new Fixture();
        f.Client.Transform = items => [items[0], items[0], items[0]];
        var result = await f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: Ct);
        Assert.False(Assert.Single(result.Rows).Passed);
        Assert.False(f.Service.CanSubmit(f.Profile.Id, Request(), result.CheckId));
    }

    [Fact]
    public async Task RemovingFailedRowsRetainsPassedSubsetForSending()
    {
        var f = new Fixture();
        var checkedResult = await f.Service.CheckAsync(f.Profile.Id, Request(Good, Retired, Shoes), cancellationToken: Ct);
        Assert.True(f.Service.CanSubmit(f.Profile.Id, Request(), checkedResult.CheckId));
        var document = await f.Service.SubmitAsync(f.Profile.Id, Request(), true, checkedResult.CheckId, Ct);
        Assert.Equal("Tracking", document.State);
        Assert.Equal(PackageOperationKind.CodeCorrection, document.OperationKind);
        Assert.Equal("productionDate", document.Action);
        Assert.Equal(string.Empty, document.PackageCode);
        Assert.Equal(1, document.ItemCount);
        Assert.Equal(1, f.Client.Creations);
    }

    [Theory]
    [InlineData("values")]
    [InlineData("group")]
    [InlineData("kind")]
    [InlineData("codes")]
    [InlineData("profile")]
    public async Task ChangedContextCannotReuseReceipt(string change)
    {
        var f = new Fixture();
        var result = await f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: Ct);
        var request = change switch
        {
            "values" => Request() with { Values = new("2026-09-02") },
            "group" => Request() with { ProductGroup = "shoes" },
            "kind" => Request() with { Kind = "expirationDate" },
            "codes" => Request(Good, Other),
            _ => Request()
        };
        var profile = change == "profile" ? f.SecondProfile.Id : f.Profile.Id;
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SubmitAsync(profile, request, true, result.CheckId, Ct));
        Assert.Equal(0, f.Client.Creations);
        Assert.Empty(f.Repository.Items);
    }

    [Fact]
    public async Task ChangedProfileCertificateInvalidatesSubmission()
    {
        var f = new Fixture();
        var result = await f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: Ct);
        f.Profile.CertificateThumbprint = new string('B', 40);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SubmitAsync(f.Profile.Id, Request(), true, result.CheckId, Ct));
        Assert.Equal(0, f.Client.Creations);
    }

    [Fact]
    public async Task MutableCallerListIsCapturedBeforeAwait()
    {
        var f = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.WaitForLookup = gate.Task;
        var codes = new List<string> { Good };
        var pending = f.Service.CheckAsync(f.Profile.Id, Request() with { Codes = codes }, cancellationToken: Ct);
        codes.Add(Retired);
        gate.SetResult();
        var result = await pending;
        Assert.Equal(Good, Assert.Single(result.Rows).Code);
        Assert.True(f.Service.CanSubmit(f.Profile.Id, Request(), result.CheckId));
        Assert.False(f.Service.CanSubmit(f.Profile.Id, Request(Good, Retired), result.CheckId));
    }

    [Fact]
    public async Task CheckedReceiptIsConsumedAfterOneSubmission()
    {
        var f = new Fixture();
        var result = await f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: Ct);
        await f.Service.SubmitAsync(f.Profile.Id, Request(), true, result.CheckId, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SubmitAsync(f.Profile.Id, Request(), true, result.CheckId, Ct));
        Assert.Equal(1, f.Client.Creations);
    }

    [Fact]
    public async Task UncheckedSubmissionDoesNotFetchCodeInformation()
    {
        var f = new Fixture();
        await f.Service.SubmitAsync(f.Profile.Id, Request(), false, null, Ct);
        Assert.Equal("true-api.code-correction", Assert.Single(f.Client.Requests).OperationId);
        Assert.Single(f.Repository.Items);
    }

    [Fact]
    public async Task CancelledCheckInvalidatesEarlierReceipt()
    {
        var f = new Fixture();
        var first = await f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Client.BeforeLookupReturn = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: cancellation.Token));
        Assert.False(f.Service.CanSubmit(f.Profile.Id, Request(), first.CheckId));
        Assert.Equal(0, f.Client.Creations);
    }

    [Fact]
    public async Task InvalidatingDuringCheckPreventsReceiptCreation()
    {
        var f = new Fixture();
        f.Client.BeforeLookupReturn = f.Service.InvalidateCheck;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: Ct));
        Assert.False(f.Service.CanSubmit(f.Profile.Id, Request(), null));
    }

    [Theory]
    [InlineData("tnved", "1234567890\n")]
    [InlineData("weight", "123\n")]
    public async Task ScalarTrailingNewlineIsRejectedWithoutSideEffects(string kind, string value)
    {
        var f = new Fixture();
        var request = Request() with { Kind = kind, Values = new(value) };
        Assert.NotEmpty(f.Service.Validate(request));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SubmitAsync(f.Profile.Id, request, false, null, Ct));
        Assert.Empty(f.Client.Requests);
        Assert.Empty(f.Repository.Items);
        Assert.Empty(f.Signer.Signed);
    }

    [Fact]
    public void CodeLimitIsExactlyOneHundredThousandUniqueCodes()
    {
        var f = new Fixture();
        var codes = Enumerable.Range(1, 100_001).Select(x => $"010460000000000021{x:0000000000000}").ToArray();
        Assert.Empty(f.Service.Validate(Request() with { Codes = codes.Take(100_000).ToArray() }));
        Assert.NotEmpty(f.Service.Validate(Request() with { Codes = codes }));
    }

    [Fact]
    public async Task LookupBatchesNeverExceedOneThousandCodes()
    {
        var f = new Fixture();
        var codes = Enumerable.Range(1, 1001).Select(x => $"010460000000000021{x:0000000000000}").ToArray();
        var result = await f.Service.CheckAsync(f.Profile.Id, Request() with { Codes = codes }, cancellationToken: Ct);
        Assert.Equal(1001, result.Rows.Count);
        Assert.Equal([1000, 1], f.Client.Requests.Select(x => JsonSerializer.Deserialize<string[]>(x.Body!)!.Length));
    }

    [Fact]
    public async Task ExactSignedUtf8BytesMatchEnvelopeAndHistoryMasksSecrets()
    {
        var f = new Fixture(production: true);
        var request = Request() with { Kind = "permitDocs", Values = new(PermitDocuments: [new(2, "RU Д-DEMO.АА00.B.00001/25", "2026-09-01")]) };
        var record = await f.Service.SubmitAsync(f.Profile.Id, request, false, null, Ct);
        var sent = Assert.Single(f.Client.Requests);
        var signed = Assert.Single(f.Signer.Signed);
        using var envelope = JsonDocument.Parse(sent.Body!);
        Assert.Equal(Encoding.UTF8.GetBytes(signed), Convert.FromBase64String(envelope.RootElement.GetProperty("product_document").GetString()!));
        Assert.Equal("CIS_INFORMATION_CHANGE", envelope.RootElement.GetProperty("type").GetString());
        Assert.Equal("MANUAL", envelope.RootElement.GetProperty("document_format").GetString());
        using var document = JsonDocument.Parse(signed);
        Assert.Equal("RU Д-DEMO.АА00.B.00001/25", document.RootElement.GetProperty("codes")[0].GetProperty("permitDocs")[0].GetProperty("permitDocNumber").GetString());
        Assert.Equal(CrptIntegrationMode.Production, record.Mode);
        var history = Assert.Single(f.Logs.Items);
        Assert.DoesNotContain("private-signature", history.RequestBody!);
        Assert.DoesNotContain("private-token", history.RequestHeaders!);
        Assert.Contains("Bearer ********", history.RequestHeaders!);
        Assert.Equal(sent.CorrelationId, history.CorrelationId);
        Assert.Equal(sent.CorrelationId, sent.Headers["X-Correlation-Id"]);
    }

    [Fact]
    public async Task CancellationAfterGuidArrivalStillPersistsGuid()
    {
        var f = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Client.BeforeCreationReturn = cancellation.Cancel;
        var result = await f.Service.SubmitAsync(f.Profile.Id, Request(), false, null, cancellation.Token);
        Assert.Equal(f.Client.DocumentId, result.DocumentId);
        Assert.Equal(f.Client.DocumentId, f.Repository.PersistedGuid);
        Assert.Equal("Tracking", result.State);
        Assert.False(f.Repository.LastUpdateToken.CanBeCanceled);
    }

    [Fact]
    public async Task AmbiguousSendStoresUnknownAndConsumesReceiptWithoutRetry()
    {
        var f = new Fixture();
        var check = await f.Service.CheckAsync(f.Profile.Id, Request(), cancellationToken: Ct);
        f.Client.CreationStatus = 503;
        var result = await f.Service.SubmitAsync(f.Profile.Id, Request(), true, check.CheckId, Ct);
        Assert.Equal("Unknown", result.State);
        Assert.Null(result.DocumentId);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SubmitAsync(f.Profile.Id, Request(), true, check.CheckId, Ct));
        Assert.Equal(1, f.Client.Creations);
    }

    private sealed class Fixture
    {
        public CrptProfile Profile { get; } = new() { Inn = "7700000000", CertificateThumbprint = new string('A', 40) };
        public CrptProfile SecondProfile { get; } = new() { Inn = "7700000001" };
        public Repository Repository { get; } = new();
        public Signer Signer { get; } = new();
        public Logs Logs { get; } = new();
        public Client Client { get; }
        public CodeCorrectionService Service { get; }
        public Fixture(bool production = false)
        {
            Client = new(production);
            var clock = new Clock();
            var history = new PackageDocumentHistory(Logs, new SensitiveDataMasker(), clock);
            Service = new(new PackageDocumentService(Repository, new Profiles(Profile, SecondProfile), Client, Signer, clock, history), Client, history, clock);
        }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Profiles(params CrptProfile[] profiles) : IProfileRepository
    {
        public Task<IReadOnlyList<CrptProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CrptProfile>>(profiles);
        public Task<CrptProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.SingleOrDefault(x => x.Id == id));
        public Task UpsertAsync(CrptProfile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Signer : ICertificateSignatureService
    {
        public List<string> Signed { get; } = [];
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SignDetachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default)
        { Signed.Add(data); return Task.FromResult("private-signature"); }
    }
    private sealed class Logs : IApiRequestLogRepository
    {
        public List<ApiRequestLog> Items { get; } = [];
        public Task AddAsync(ApiRequestLog entry, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Items.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<ApiRequestLog>> ListAsync(Guid profileId, int take, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ApiRequestLog>>(Items.Where(x => x.ProfileId == profileId).Take(take).ToArray());
        public Task<ApiRequestLog?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(x => x.ProfileId == profileId && x.Id == id));
    }
    private sealed class Repository : IPackageDocumentRepository
    {
        public List<PackageDocument> Items { get; } = [];
        public string? PersistedGuid { get; private set; }
        public CancellationToken LastUpdateToken { get; private set; }
        public Task AddAsync(PackageDocument document, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Items.Add(document); return Task.CompletedTask; }
        public Task UpdateAsync(PackageDocument document, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); LastUpdateToken = cancellationToken; PersistedGuid = document.DocumentId; return Task.CompletedTask; }
        public Task<IReadOnlyList<PackageDocument>> ListAsync(Guid profileId, PackageOperationKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageDocument>>(Items.Where(x => x.ProfileId == profileId && x.OperationKind == kind).ToArray());
        public Task<PackageDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task<bool> ScheduleAsync(Guid profileId, Guid id, PackageOperationKind kind, long nowUnixMs, bool resume, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CompleteCheckAsync(PackageDocument document, long leaseUntilUnixMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PackageDocument>> ClaimDueAsync(long nowUnixMs, int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Client(bool production) : ITrueApiClient
    {
        public Uri BaseAddress { get; } = new(production ? "https://markirovka.crpt.ru/api/v3/true-api/" : "https://trueapi.mock.invalid");
        public List<IntegrationRequest> Requests { get; } = [];
        public int Creations => Requests.Count(x => x.OperationId == "true-api.code-correction");
        public string DocumentId { get; } = Guid.NewGuid().ToString();
        public int CreationStatus { get; set; } = 201;
        public Func<CodeInfoItem[], CodeInfoItem[]>? Transform { get; set; }
        public Action? BeforeLookupReturn { get; set; }
        public Action? BeforeCreationReturn { get; set; }
        public Task? WaitForLookup { get; set; }
        public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var lookup = request.OperationId == "true-api.code-info";
            string body;
            if (lookup)
            {
                if (WaitForLookup is not null) await WaitForLookup.WaitAsync(cancellationToken);
                var codes = JsonSerializer.Deserialize<string[]>(request.Body!)!;
                var items = codes.Select(code => new CodeInfoItem(new CisInfo
                {
                    Cis = code, RequestedCis = code, ProductGroup = code == Shoes ? "shoes" : "milk", ProductGroupId = code == Shoes ? 2 : 8,
                    GeneralPackageType = "UNIT", PackageType = "UNIT", Status = code == Retired ? "RETIRED" : "INTRODUCED",
                    StatusEx = "EMPTY", EmissionType = "LOCAL", Gtin = "04600000000000"
                })).ToArray();
                body = JsonSerializer.Serialize(Transform?.Invoke(items) ?? items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                BeforeLookupReturn?.Invoke();
            }
            else { body = CreationStatus == 201 ? JsonSerializer.Serialize(DocumentId) : "{}"; BeforeCreationReturn?.Invoke(); }
            return new(lookup ? 200 : CreationStatus, request.Method, request.Url,
                new Dictionary<string, string>(request.Headers) { ["Authorization"] = "Bearer private-token" }, request.Body,
                new Dictionary<string, string> { ["X-CRPT-Workbench-Mock"] = "true" }, body, TimeSpan.FromMilliseconds(1));
        }
    }
}
