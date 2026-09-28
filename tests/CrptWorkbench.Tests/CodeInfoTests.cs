using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class CodeInfoTests
{
    public const string First = "010460000000000021DEMO000000001";
    public const string Second = "010460000000000021DEMO000000002";
    public const string Group = "046000000000000001";
    public const string Box = "000000000000000001";
    public const string Unknown = "010460000000000021UNKNOWN000001";
    internal static Dictionary<string, string> Input(string codes) => new() { ["codes"] = codes };
    private static ApiOperationContext Context => new(new(), "code-info-test");

    [Fact]
    public async Task PostPreservesArrayOrderDuplicatesAndEscapesJsonWithoutQuery()
    {
        var client = new CapturingClient();
        var handler = new TrueApiCodeInfoHandler(client);
        const string special = "010460000000000021\"\\& +?Ж";
        await handler.ExecuteAsync(Input($"{First}\r\n\n{special}\r{First}"), Context, TestContext.Current.CancellationToken);
        var request = Assert.IsType<IntegrationRequest>(client.Request);
        Assert.Equal("POST", request.Method);
        Assert.Equal("true-api.code-info", request.OperationId);
        Assert.Equal("https://production.trueapi.mock.invalid/cises/info", request.Url);
        Assert.Equal(new[] { First, special, First }, JsonSerializer.Deserialize<string[]>(request.Body!));
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.Equal(Context.CorrelationId, request.Headers["X-Correlation-Id"]);
        Assert.Equal(Context.CorrelationId, request.CorrelationId);
        Assert.Single(handler.Descriptor.Fields);
        Assert.True(handler.Descriptor.Fields[0].Multiline);
    }

    [Fact]
    public async Task ClientBaseAddressIsUsedAndProfileCannotOverrideIt()
    {
        var client = new CapturingClient { BaseAddress = new("https://custom.mock.invalid/api/v3/") };
        var context = new ApiOperationContext(new() { TrueApiProductionBaseUrl = "https://untrusted.example" }, "test");
        await new TrueApiCodeInfoHandler(client).ExecuteAsync(Input(First), context, TestContext.Current.CancellationToken);
        Assert.Equal("https://custom.mock.invalid/api/v3/cises/info", client.Request!.Url);
    }

    [Fact]
    public async Task PackageInputMakesExactlyOneClientRequest()
    {
        var client = new CapturingClient();
        await new TrueApiCodeInfoHandler(client).ExecuteAsync(Input(Box), Context, TestContext.Current.CancellationToken);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(new[] { Box }, JsonSerializer.Deserialize<string[]>(client.Request!.Body!));
    }

    [Fact]
    public void ParserAcceptsInclusiveBoundariesAndDoesNotTrimCodes()
    {
        var codes = new[] { new string('a', 18), new string('b', 74), " " + First + " " };
        Assert.Equal(codes, TrueApiCodeInfoHandler.Parse(Input(string.Join('\n', codes))).Codes);
        Assert.Equal(1000, TrueApiCodeInfoHandler.Parse(Input(string.Join('\n', Enumerable.Repeat(First, 1000)))).Codes.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n \r\n")]
    public void EmptyInputIsRejected(string input) => Assert.Throws<ArgumentException>(() => TrueApiCodeInfoHandler.Parse(Input(input)));

    [Fact]
    public void TooManyCodesAreRejected() => Assert.Throws<ArgumentException>(() =>
        TrueApiCodeInfoHandler.Parse(Input(string.Join('\n', Enumerable.Repeat(First, 1001)))));

    [Theory]
    [InlineData(17)]
    [InlineData(75)]
    public void InvalidLengthsReportOriginalLineNumber(int length)
    {
        var error = Assert.Throws<ArgumentException>(() => TrueApiCodeInfoHandler.Parse(Input($"{First}\n\n{new string('a', length)}")));
        Assert.Contains("Строка 3", error.Message);
    }

    [Theory]
    [InlineData("(01)04600000000000(21)DEMO000000001")]
    [InlineData("0104600000000000(21)DEMO000000001")]
    public void BracketedIdentifiersAreRejected(string code) => Assert.Contains("без скобок",
        Assert.Throws<ArgumentException>(() => TrueApiCodeInfoHandler.Parse(Input(code))).Message);

    [Fact]
    public async Task InvalidInputDoesNotReachClient()
    {
        var client = new CapturingClient();
        await Assert.ThrowsAsync<ArgumentException>(() => new TrueApiCodeInfoHandler(client).ExecuteAsync(Input("bad"), Context, TestContext.Current.CancellationToken));
        Assert.Null(client.Request);
    }

    [Theory]
    [InlineData(First + "\n" + Second, 200, 2, 0)]
    [InlineData(First + "\n" + Unknown + "\n" + First, 200, 2, 1)]
    [InlineData(Unknown, 404, 0, 1)]
    public async Task MockReportsPerCodeOutcomes(string input, int status, int found, int errors)
    {
        var exchange = await new TrueApiCodeInfoHandler(new MockTrueApiClient()).ExecuteAsync(Input(input), Context, TestContext.Current.CancellationToken);
        Assert.Equal(status, exchange.StatusCode);
        Assert.True(exchange.Duration > TimeSpan.Zero);
        Assert.Equal("true", exchange.ResponseHeaders["X-CRPT-Workbench-Mock"]);
        Assert.Equal(Context.CorrelationId, exchange.ResponseHeaders["X-Correlation-Id"]);
        var results = Assert.IsType<CodeInfoResults>(CodeInfoResults.Parse(exchange.ResponseBody));
        Assert.Equal(found, results.FoundCount);
        Assert.Equal(errors, results.ErrorCount);
        Assert.Equal(input.Split('\n'), results.Items.Select(x => x.CisInfo!.RequestedCis));
    }

    [Fact]
    public async Task MockSupportsUnitGroupAndBoxRelationshipsAndDistinctStatuses()
    {
        var handler = new TrueApiCodeInfoHandler(new MockTrueApiClient());
        var exchange = await handler.ExecuteAsync(Input(string.Join('\n', First, Second, Group, Box)), Context, TestContext.Current.CancellationToken);
        var results = CodeInfoResults.Parse(exchange.ResponseBody)!;
        Assert.Equal(new[] { "INTRODUCED", "RETIRED", "INTRODUCED", "INTRODUCED" }, results.Items.Select(x => x.CisInfo!.Status));
        Assert.Equal(new[] { "UNIT", "UNIT", "GROUP", "BOX" }, results.Items.Select(x => x.CisInfo!.GeneralPackageType));
        Assert.Equal(Group, results.Items[0].CisInfo!.Parent);
        Assert.Equal(new[] { First, Second }, results.Items[2].CisInfo!.Child);
        Assert.Equal(Box, results.Items[2].CisInfo!.Parent);
        Assert.Equal(new[] { Group }, results.Items[3].CisInfo!.Child);
        Assert.Null(results.Items[3].CisInfo!.ProductName);
        Assert.Null(results.Items[3].CisInfo!.Gtin);
        Assert.Equal(2, results.Items[0].CisInfo!.Expirations!.Count);
        Assert.Single(results.Items[0].CisInfo!.CertDoc!);
        Assert.NotNull(results.Items[1].CisInfo!.CertDocRequirementDetails);
    }

    [Fact]
    public async Task MockLargeTransportBoxContainsOneHundredResolvableUnits()
    {
        const string box = "000000000000000100";
        var handler = new TrueApiCodeInfoHandler(new MockTrueApiClient());
        var boxExchange = await handler.ExecuteAsync(Input(box), Context, TestContext.Current.CancellationToken);
        var package = Assert.Single(CodeInfoResults.Parse(boxExchange.ResponseBody)!.Items).CisInfo!;
        Assert.Equal("BOX", package.GeneralPackageType);
        var children = Assert.IsAssignableFrom<IReadOnlyList<string>>(package.Child);
        Assert.Equal(100, children.Count);
        Assert.Equal(100, children.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("010460000000000021BULK000000001", children[0]);
        Assert.Equal("010460000000000021BULK000000100", children[^1]);

        var unitExchange = await handler.ExecuteAsync(Input($"{children[0]}\n{children[^1]}"), Context, TestContext.Current.CancellationToken);
        var units = CodeInfoResults.Parse(unitExchange.ResponseBody)!;
        Assert.Equal(2, units.FoundCount);
        Assert.All(units.Items, item =>
        {
            Assert.Equal("UNIT", item.CisInfo!.GeneralPackageType);
            Assert.Equal(box, item.CisInfo.Parent);
        });
    }

    [Fact]
    public async Task MockDocumentHistoryDistinguishesPaymentAndKeepsTechnicalExchanges()
    {
        var exchange = await new TrueApiCodeInfoHandler(new MockTrueApiClient()).ExecuteAsync(
            Input($"{First}\n{Second}"), Context, TestContext.Current.CancellationToken);
        var items = CodeInfoResults.Parse(exchange.ResponseBody)!.Items;
        Assert.True(items[0].DocumentHistory!.Paid);
        Assert.Contains(items[0].DocumentHistory!.Documents, document =>
            document.Type == "PAYMENT" && document.SenderInn == "0000000000");
        Assert.False(items[1].DocumentHistory!.Paid);
        Assert.DoesNotContain(items[1].DocumentHistory!.Documents, document => document.Type == "PAYMENT");
        Assert.All(exchange.Exchanges!, item =>
        {
            Assert.Equal(Context.CorrelationId, item.ResponseHeaders["X-Correlation-Id"]);
            Assert.Equal("true", item.ResponseHeaders["X-CRPT-Workbench-Mock"]);
        });
    }

    [Fact]
    public async Task HistoryFailureDoesNotTurnUnknownPaymentIntoNo()
    {
        var exchange = await new TrueApiCodeInfoHandler(new HistoryFailureClient()).ExecuteAsync(
            Input(First), Context, TestContext.Current.CancellationToken);
        var item = Assert.Single(CodeInfoResults.Parse(exchange.ResponseBody)!.Items);
        Assert.True(CodeInfoResults.IsFound(item));
        Assert.Null(item.DocumentHistory!.Paid);
        Assert.Contains("503", item.DocumentHistory.Warning);
        Assert.Equal(2, exchange.Exchanges!.Count);
    }

    [Fact]
    public void ResultReaderHandlesNestedErrorsMissingFieldsAndUnknownStatus()
    {
        const string body = "[{\"cisInfo\":{\"requestedCis\":\"one\",\"status\":\"FUTURE_STATUS\",\"custom\":42}},{\"cisInfo\":{\"requestedCis\":\"two\"},\"errorCode\":\"404\",\"errorMessage\":\"КИ не найден\"}]";
        var result = CodeInfoResults.Parse(body)!;
        Assert.Equal(1, result.FoundCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.Null(result.Items[0].CisInfo!.OwnerName);
        Assert.Equal(42, result.Items[0].CisInfo!.AdditionalFields!["custom"].GetInt32());
        Assert.Equal("FUTURE_STATUS", CodeInfoResults.StatusLabel(result.Items[0].CisInfo!.Status));
        Assert.Equal("В обороте", CodeInfoResults.StatusLabel("INTRODUCED"));
        Assert.Null(CodeInfoResults.Parse("{\"error_message\":\"failure\"}"));
        Assert.Null(CodeInfoResults.Parse("not json"));
        Assert.Null(CodeInfoResults.Parse("[null]"));
    }

    [Fact]
    public void ResultReaderParsesPackageDocumentsRequirementsAndShelfLives()
    {
        const string body = """
            [{"cisInfo":{"requestedCis":"root","generalPackageType":"BOX","packageType":"UNIT","child":["child-1"],"parent":"parent-1","certDoc":[{"type":"CONFORMITY_DECLARATION","number":"RU D-DEMO","date":"2025.05.20","wellNumber":"W-1","indx":123,"statusGroup":1}],"certDocRequirementDetails":{"notRequiredReason":"reason","isRequired":true},"expirations":[{"expirationStorageDate":"2025-06-12T00:00:00Z","storageConditionId":7,"storageConditionName":"cool"}]}}]
            """;
        var info = CodeInfoResults.Parse(body)!.Items.Single().CisInfo!;
        Assert.Equal("BOX", CodeInfoResults.PackageTypeCode(info));
        Assert.Equal("Транспортная упаковка", CodeInfoResults.PackageTypeLabel(info));
        Assert.Equal(new[] { "child-1" }, info.Child);
        Assert.Equal("parent-1", info.Parent);
        var document = Assert.Single(info.CertDoc!);
        Assert.Equal("CONFORMITY_DECLARATION", document.Type);
        Assert.Equal("RU D-DEMO", document.Number);
        Assert.Equal("2025.05.20", document.Date);
        Assert.Equal("W-1", document.WellNumber);
        Assert.Equal(123, document.Indx);
        Assert.Equal(1, document.StatusGroup);
        Assert.True(info.CertDocRequirementDetails!.IsRequired);
        Assert.Equal("reason", info.CertDocRequirementDetails.NotRequiredReason);
        var expiration = Assert.Single(info.Expirations!);
        Assert.Equal(7, expiration.StorageConditionId);
        Assert.Equal("cool", expiration.StorageConditionName);
        Assert.Equal(new DateTimeOffset(2025, 6, 12, 0, 0, 0, TimeSpan.Zero), expiration.ExpirationStorageDate);
    }

    [Theory]
    [InlineData("UNIT", "Единица товара")]
    [InlineData("GROUP", "Групповая упаковка")]
    [InlineData("SET", "Набор")]
    [InlineData("BUNDLE", "Комплект")]
    [InlineData("BOX", "Транспортная упаковка")]
    [InlineData("ATK", "Агрегированный таможенный код")]
    [InlineData("LEVEL1", "Транспортная упаковка 1-го уровня")]
    [InlineData("LEVEL2", "Транспортная упаковка 2-го уровня")]
    [InlineData("LEVEL3", "Транспортная упаковка 3-го уровня")]
    [InlineData("LEVEL4", "Транспортная упаковка 4-го уровня")]
    [InlineData("LEVEL5", "Транспортная упаковка 5-го уровня")]
    [InlineData("FUTURE_TYPE", "FUTURE_TYPE")]
    public void PackageTypesHaveFriendlyLabelsAndPreserveUnknownValues(string code, string label)
    {
        var info = new CisInfo { GeneralPackageType = code };
        Assert.Equal(code, CodeInfoResults.PackageTypeCode(info));
        Assert.Equal(label, CodeInfoResults.PackageTypeLabel(info));
    }

    [Fact]
    public void LegacyPackageTypeIsUsedOnlyAsFallback()
    {
        Assert.Equal("Групповая упаковка", CodeInfoResults.PackageTypeLabel(new CisInfo { PackageType = "GROUP" }));
        Assert.Equal("Транспортная упаковка", CodeInfoResults.PackageTypeLabel(new CisInfo { GeneralPackageType = "BOX", PackageType = "UNIT" }));
    }

    [Fact]
#pragma warning disable xUnit1051 // This test intentionally supplies and cancels its own token.
    public async Task CancellationIsForwardedAndMockDelayIsCancelable()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new CapturingClient();
        await new TrueApiCodeInfoHandler(client).ExecuteAsync(Input(First), Context, cancellation.Token);
        Assert.Equal(cancellation.Token, client.Token);
        var task = new TrueApiCodeInfoHandler(new MockTrueApiClient()).ExecuteAsync(Input(First), Context, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        client.Request = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TrueApiCodeInfoHandler(client).ExecuteAsync(Input(First), Context, cancellation.Token));
        Assert.Null(client.Request);
    }
#pragma warning restore xUnit1051

    private sealed class CapturingClient : ITrueApiClient
    {
        public Uri BaseAddress { get; set; } = new("https://production.trueapi.mock.invalid");
        public IntegrationRequest? Request { get; set; }
        public CancellationToken Token { get; private set; }
        public int CallCount { get; private set; }
        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            Token = cancellationToken;
            return Task.FromResult(new IntegrationExchange(200, request.Method, request.Url, request.Headers, request.Body, new Dictionary<string, string>(), "[]", TimeSpan.Zero));
        }
    }

    private sealed class HistoryFailureClient : ITrueApiClient
    {
        public Uri BaseAddress { get; } = new("https://production.trueapi.mock.invalid");

        public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            var history = request.Url.Contains("/cises/history?", StringComparison.Ordinal);
            var body = history ? "{}" : JsonSerializer.Serialize(new[] { new CodeInfoItem(new CisInfo { Cis = First, RequestedCis = First }) });
            return Task.FromResult(new IntegrationExchange(history ? 503 : 200, request.Method, request.Url,
                request.Headers, request.Body, new Dictionary<string, string>(), body, TimeSpan.FromMilliseconds(1)));
        }
    }
}
