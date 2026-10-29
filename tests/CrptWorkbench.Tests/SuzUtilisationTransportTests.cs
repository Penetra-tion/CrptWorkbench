using System.Net;
using System.Text;
using System.Text.Json;
using CrptWorkbench.Clients;
using CrptWorkbench.Domain;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzUtilisationTransportTests
{
    private const string Oms = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Code = "0104604060002439215CQ9F6alYHJNc\u001d91FFD0\u001d92CRYPTO-SECRET";
    private static IntegrationRequest Creation(string group = "toys", string scenario = "UTILISATION", int count = 1) => new("suz.utilisation", "POST", "api/v3/utilisation?omsId=" + Oms,
        new Dictionary<string, string> { ["X-Signature"] = "SIGNATURE-SECRET" }, JsonSerializer.Serialize(new { productGroup = group, utilisationType = scenario, sntins = Enumerable.Repeat(Code, count).ToArray() }), "test-correlation", SuzTransportTests.Auth, SuzTransportTests.Connection);
    private sealed class Tokens : ISuzTokenProvider
    {
        public int Calls; public int Invalidations;
        public Task<string> GetTokenAsync(IntegrationAuthenticationContext auth, SuzConnectionContext connection, string correlationId, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult("TOKEN-SECRET"); }
        public void Invalidate(IntegrationAuthenticationContext auth, SuzConnectionContext connection, string token) => Invalidations++;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedCreationCancellationRemainsDefinitelyNotDispatched(bool cancelOuterToken)
    {
        var limiter = new ApiRequestLimiter();
        var slots = new List<IDisposable>();
        var sent = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new ApiConcurrencyHandler(limiter, ApiConcurrencyService.Suz)
        {
            InnerHandler = new SuzTransportTests.Handler(_ => { sent++; return SuzTransportTests.Reply(200, "{}"); })
        };
        try
        {
            for (var i = 0; i < 4; i++) slots.Add(await limiter.AcquireAsync(ApiConcurrencyService.Suz, TestContext.Current.CancellationToken));
            var client = new RealSuzClient(new SuzTransportTests.Factory(handler), new Tokens(), SuzTransportTests.OptionsFor());
            var stopped = false;
            var pending = client.ExecuteAsync(Creation() with
            {
                BeforeDispatchAsync = _ => stopped ? throw new OperationCanceledException("Another document failed.") : Task.CompletedTask
            }, cancellation.Token);
            Assert.False(pending.IsCompleted);
            stopped = true;
            if (cancelOuterToken) cancellation.Cancel();
            slots[0].Dispose();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(ApiDispatchNotStartedException.IsPresent(exception));
            Assert.Equal(0, sent);
        }
        finally { foreach (var slot in slots) slot.Dispose(); }
    }

    [Theory]
    [InlineData(CrptAuthenticationMode.Auto)]
    [InlineData(CrptAuthenticationMode.Manual)]
    public async Task CreationSendsExactBytesAndNeverRetries401(CrptAuthenticationMode mode)
    {
        var request = Creation();
        var calls = 0;
        var tokens = new Tokens();
        var factory = new SuzTransportTests.Factory(new SuzTransportTests.Handler(async message =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(Encoding.UTF8.GetBytes(request.Body!), await message.Content!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal("SIGNATURE-SECRET", message.Headers.GetValues("X-Signature").Single());
            Assert.Equal("TOKEN-SECRET", message.Headers.GetValues("clientToken").Single());
            return await SuzTransportTests.Reply(401, JsonSerializer.Serialize(new { description = Code, reportId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" }));
        }));
        var exchange = await new RealSuzClient(factory, tokens, Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production, SuzAuthMode = mode })).ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(1, tokens.Calls); Assert.Equal(0, tokens.Invalidations);
        Assert.Equal("<body omitted>", exchange.RequestBody);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(exchange));
    }
    [Theory]
    [InlineData("pharma", "UTILISATION", 1)]
    [InlineData("lp", "UTILISATION", 1)]
    [InlineData("toys", "RESORT", 1)]
    [InlineData("toys", "UTILISATION", 0)]
    [InlineData("toys", "UTILISATION", 30001)]
    public async Task InvalidBodyBlocksBeforeAuthentication(string group, string scenario, int count)
    {
        var tokens = new Tokens();
        var client = new RealSuzClient(new SuzTransportTests.Factory(new SuzTransportTests.Handler(_ => throw new Exception("network must not run"))), tokens, SuzTransportTests.OptionsFor());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Creation(group, scenario, count), TestContext.Current.CancellationToken));
        Assert.Equal(0, tokens.Calls);
    }
    [Theory]
    [InlineData("api/v3/utilisation?omsId=" + Oms + "&extra=1", "suz.utilisation", true)]
    [InlineData("api/v3/utilisation?omsId=" + Oms, "wrong.operation", true)]
    [InlineData("api/v3/utilisation?omsId=" + Oms, "suz.utilisation", false)]
    public async Task InvalidRoutingOrSignatureBlocksBeforeAuthentication(string path, string operation, bool signature)
    {
        var tokens = new Tokens();
        var client = new RealSuzClient(new SuzTransportTests.Factory(new SuzTransportTests.Handler(_ => throw new Exception("network"))), tokens, SuzTransportTests.OptionsFor());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync(Creation() with { Url = path, OperationId = operation, Headers = signature ? Creation().Headers : new Dictionary<string, string>() }, TestContext.Current.CancellationToken));
        Assert.Equal(0, tokens.Calls);
    }
    [Fact]
    public async Task NetworkGuardOnlyPassesSignedExactCreationAndReportReads()
    {
        using var client = new HttpClient(new ProductionSuzNetworkGuardHandler { InnerHandler = new SuzTransportTests.Handler(_ => SuzTransportTests.Reply(200, "{}")) });
        using var valid = new HttpRequestMessage(HttpMethod.Post, "https://suzgrid.crpt.ru/" + Creation().Url) { Content = new StringContent(Creation().Body!, Encoding.UTF8, "application/json") };
        valid.Headers.Add("X-Signature", "signature");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(valid, TestContext.Current.CancellationToken)).StatusCode);
        using var unsigned = new HttpRequestMessage(HttpMethod.Post, "https://suzgrid.crpt.ru/" + Creation().Url) { Content = new StringContent(Creation().Body!, Encoding.UTF8, "application/json") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(unsigned, TestContext.Current.CancellationToken));
        using var report = new HttpRequestMessage(HttpMethod.Get, "https://suzgrid.crpt.ru/api/v3/report/info?omsId=" + Oms + "&reportId=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(report, TestContext.Current.CancellationToken)).StatusCode);
    }
    [Fact]
    public async Task LegalNumericAndStructuredAttributesReachTransportAsExactJson()
    {
        var request = Creation("milk") with { Body = JsonSerializer.Serialize(new { productGroup = "milk", sntins = new[] { "010464001674838921000001\u001d93dGVz" }, attributes = new { usedInProduction = 1, productionDate = "2026-10-01", expDateConditions = new Dictionary<string,string> { ["1"] = "2026-10-10" } } }) };
        var sent = 0;
        var factory = new SuzTransportTests.Factory(new SuzTransportTests.Handler(message =>
        {
            sent++;
            Assert.Equal(request.Body, message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult());
            return SuzTransportTests.Reply(200, "{\"omsId\":\"" + Oms + "\",\"reportId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\"}");
        }));
        var exchange = await new RealSuzClient(factory, new Tokens(), SuzTransportTests.OptionsFor()).ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, sent);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal("<body omitted>", exchange.RequestBody);
    }
    [Fact]
    public async Task MockReportIdentitySurvivesClientRecreationAndReceiptCompletes()
    {
        var first = await new MockSuzClient().ExecuteAsync(Creation(), TestContext.Current.CancellationToken);
        var second = await new MockSuzClient().ExecuteAsync(Creation(), TestContext.Current.CancellationToken);
        Assert.Equal(first.ResponseBody, second.ResponseBody);
        Assert.Equal("<body omitted>", first.RequestBody);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(first));
        var id = JsonDocument.Parse(first.ResponseBody!).RootElement.GetProperty("reportId").GetString();
        var report = await new MockSuzClient().ExecuteAsync(Creation() with { OperationId = "suz.report-info", Method = "GET", Url = "api/v3/report/info?omsId=" + Oms + "&reportId=" + id, Body = null }, TestContext.Current.CancellationToken);
        Assert.Contains("SUCCESS", report.ResponseBody);
        var receipt = await new MockSuzClient().ExecuteAsync(Creation() with { OperationId = "suz.document-status", Method = "GET", Url = "https://production.suz.mock.invalid/api/v3/receipts/receipt?omsId=" + Oms + "&resultDocId=" + id, Body = null, Headers = new Dictionary<string, string>() }, TestContext.Current.CancellationToken);
        Assert.Contains("SUCCESS", receipt.ResponseBody);
    }
    [Theory]
    [InlineData(CrptAuthenticationMode.Auto, 2, 1)]
    [InlineData(CrptAuthenticationMode.Manual, 1, 0)]
    public async Task OnlyAutoReportReadRetries401(CrptAuthenticationMode mode, int calls, int invalidations)
    {
        var tokens = new Tokens(); var sent = 0;
        var factory = new SuzTransportTests.Factory(new SuzTransportTests.Handler(_ => { sent++; return SuzTransportTests.Reply(401, "{}"); }));
        await new RealSuzClient(factory, tokens, Options.Create(new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production, SuzAuthMode = mode })).ExecuteAsync(Creation() with { OperationId = "suz.report-info", Method = "GET", Url = "api/v3/report/info?omsId=" + Oms + "&reportId=bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", Body = null }, TestContext.Current.CancellationToken);
        Assert.Equal(calls, sent); Assert.Equal(invalidations, tokens.Invalidations);
    }
}
