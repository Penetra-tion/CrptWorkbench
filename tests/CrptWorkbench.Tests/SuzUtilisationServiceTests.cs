using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzUtilisationServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task PreparationUsesOnlyActiveProfileBusinessPlace()
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var service = new SuzUtilisationService(f.Repository, f.Profiles, new Client(r => Exchange(r, 200, "{}")), new Signer(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var code = "010464001674838921" + new string('A', 20) + "\u001d93dGVz";
        var values = new Dictionary<string, string> { ["_participantKind"] = "LEGAL", ["kpp"] = "999999999" };
        var request = new SuzUtilisationRequest("radio", "GROUP", "UTILISATION", values, [new(code, new Dictionary<string, string>())]);
        Assert.Contains((await service.PrepareAsync(f.Profile.Id, request, Ct)).Errors, x => x.Field == "kpp");
        f.Profile.Kpp = "123456789"; f.Profile.FiasId = Guid.NewGuid().ToString();
        await f.Profiles.UpsertAsync(f.Profile, Ct);
        var legal = await service.PrepareAsync(f.Profile.Id, request, Ct);
        Assert.True(legal.IsValid, string.Join(";", legal.Errors.Select(x => x.Message)));
        using (var json = JsonDocument.Parse(legal.Reports.Single().Json))
        {
            Assert.Equal("123456789", json.RootElement.GetProperty("attributes").GetProperty("kpp").GetString());
            Assert.False(json.RootElement.GetProperty("attributes").TryGetProperty("fiasId", out _));
        }
        values["_participantKind"] = "PERSON";
        values["fiasId"] = Guid.NewGuid().ToString();
        var person = await service.PrepareAsync(f.Profile.Id, request, Ct);
        Assert.True(person.IsValid);
        using var personJson = JsonDocument.Parse(person.Reports.Single().Json);
        Assert.Equal(f.Profile.FiasId, personJson.RootElement.GetProperty("attributes").GetProperty("fiasId").GetString());
        Assert.False(personJson.RootElement.GetProperty("attributes").TryGetProperty("kpp", out _));
        var another = new CrptProfile { Name = "Другой профиль", Kpp = "987654321" };
        await f.Profiles.UpsertAsync(another, Ct);
        values["_participantKind"] = "LEGAL";
        var otherResult = await service.PrepareAsync(another.Id, request, Ct);
        using var otherJson = JsonDocument.Parse(otherResult.Reports.Single().Json);
        Assert.Equal("987654321", otherJson.RootElement.GetProperty("attributes").GetProperty("kpp").GetString());
        f.Profile.Kpp = null;
        await f.Profiles.UpsertAsync(f.Profile, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(f.Profile.Id, request, Ct));
    }
    [Fact]
    public async Task GuidSurvivesCancellationAndFollowingPartStaysPreparedWithSecretFreeHistory()
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        using var cancel = new CancellationTokenSource();
        var job = f.Job(); job.CorrelationId = Guid.NewGuid().ToString("N");
        var first = new SuzUtilisationReport { JobId = job.Id, Number = 1, CodeCount = 30000 };
        var second = new SuzUtilisationReport { JobId = job.Id, Number = 2, CodeCount = 1 };
        await f.Repository.AddAsync(job, [new(first, "{\"secret\":\"CRYPTO-TAIL\"}"), new(second, "{}")], Ct);
        var client = new Client(r => { cancel.Cancel(); return Exchange(r, 200, JsonSerializer.Serialize(new { omsId = SuzDocumentStatusHandler.MockOmsId, reportId = Guid.NewGuid() })); });
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ContinueAsync(f.Profile.Id, job.Id, cancel.Token));
        var saved = (await service.GetAsync(f.Profile.Id, job.Id, Ct))!;
        Assert.Equal("Tracking", saved.Reports[0].State); Assert.NotNull(saved.Reports[0].ReportId);
        Assert.Equal("Prepared", saved.Reports[1].State); Assert.Single(client.Requests);
        var history = Assert.Single(await f.Logs.ListAsync(f.Profile.Id, 10, Ct));
        Assert.DoesNotContain("CRYPTO-TAIL", history.RequestBody); Assert.DoesNotContain("CRYPTO-TAIL", history.ResponseBody);
        Assert.Equal(job.CorrelationId, history.CorrelationId);
    }
    [Fact]
    public async Task UnknownResponseStopsSequenceAndContinuationNeverResendsUnknown()
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var job = f.Job();
        await f.Repository.AddAsync(job, Enumerable.Range(1, 2).Select(i => new SuzUtilisationDraft(new() { JobId = job.Id, Number = i, CodeCount = 1 }, "{}")).ToArray(), Ct);
        var client = new Client(r => Exchange(r, 200, "{\"reportId\":\"invalid\"}"));
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var details = await service.ContinueAsync(f.Profile.Id, job.Id, Ct);
        Assert.Equal("Unknown", details.Reports[0].State); Assert.Equal("Prepared", details.Reports[1].State);
        client.Handler = r => Exchange(r, 200, JsonSerializer.Serialize(new { omsId = SuzDocumentStatusHandler.MockOmsId, reportId = Guid.NewGuid() }));
        details = await service.ContinueAsync(f.Profile.Id, job.Id, Ct);
        Assert.Equal("Unknown", details.Reports[0].State); Assert.Equal("Tracking", details.Reports[1].State);
        Assert.Equal(2, client.Requests.Count);
    }
    [Fact]
    public async Task ProductionSignsExactJsonAndChangedConnectionBlocksContinuation()
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        f.Profile.SuzOmsId = Guid.NewGuid().ToString(); f.Profile.SuzConnectionId = Guid.NewGuid().ToString(); f.Profile.CertificateThumbprint = "thumb";
        await f.Profiles.UpsertAsync(f.Profile, Ct);
        var job = f.Job(); job.Mode = CrptIntegrationMode.Production; job.OmsId = f.Profile.SuzOmsId; job.ConnectionId = f.Profile.SuzConnectionId; job.CertificateThumbprint = "thumb";
        const string json = "{ \"sntins\": [\"КОД\\u001dCRYPTO\"] }";
        await f.Repository.AddAsync(job, [new(new() { JobId = job.Id, Number = 1, CodeCount = 1 }, json)], Ct);
        var signer = new Signer(); var client = new Client(r => Exchange(r, 200, JsonSerializer.Serialize(new { omsId = job.OmsId, reportId = Guid.NewGuid() })));
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, signer, f.Logs, () => CrptIntegrationMode.Production, TimeProvider.System);
        f.Profile.SuzConnectionId = Guid.NewGuid().ToString(); await f.Profiles.UpsertAsync(f.Profile, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ContinueAsync(f.Profile.Id, job.Id, Ct)); Assert.Empty(client.Requests);
        f.Profile.SuzConnectionId = job.ConnectionId; await f.Profiles.UpsertAsync(f.Profile, Ct);
        await service.ContinueAsync(f.Profile.Id, job.Id, Ct);
        Assert.Equal(json, signer.Json); Assert.Equal(json, Assert.Single(client.Requests).Body);
        Assert.Equal("signature", client.Requests[0].Headers["X-Signature"]);
    }
    [Theory]
    [InlineData("SUCCESS", "Finished")]
    [InlineData("FAILED", "Finished")]
    [InlineData("IN_PROGRESS", "Tracking")]
    [InlineData("SUCCESS", "Finished", "PARTIALLY")]
    public async Task ReportSuccessWithoutFinalRnmsReceiptRemainsTrackingAndReceiptCompletes(string nestedState, string expectedState, string remoteStatus = "SUCCESS")
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var job = f.Job(); var id = Guid.NewGuid().ToString();
        var report = new SuzUtilisationReport { JobId = job.Id, Number = 1, State = "Tracking", ReportId = id, SubmittedAtUnixMs = 0, NextCheckAtUnixMs = 0, TrackingUntilUnixMs = long.MaxValue };
        await f.Repository.AddAsync(job, [new(report, "{}")], Ct);
        var final = false;
        var client = new Client(r => r.OperationId == "suz.report-info" ? Exchange(r, 200, JsonSerializer.Serialize(new { omsId = SuzDocumentStatusHandler.MockOmsId, reportId = id, status = remoteStatus })) : Exchange(r, 200,
            JsonSerializer.Serialize(new { results = new[] { new { resultDocId = id, resultDocDate = 1, sourceDocId = id, sourceDocDate = 1, state = "SUCCESS", code = 0, description = "safe", operations = new[] { new { operationId = Guid.NewGuid().ToString(), operationType = final ? "RNMS_GIS_PROCESSED" : "REPORT_UTILIZE_RECEIVED", operationDate = 1, details = new { state = nestedState } } } } } })));
        var clock = new Clock(); var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => CrptIntegrationMode.Mock, clock);
        var tracker = new SuzUtilisationTracker(f.Repository, f.Profiles, client, service, () => CrptIntegrationMode.Mock, clock);
        await tracker.PollDueAsync(Ct);
        Assert.Equal(remoteStatus == "PARTIALLY" ? "Finished" : "Tracking", (await service.GetAsync(f.Profile.Id, job.Id, Ct))!.Reports[0].State);
        final = true; clock.Now = clock.Now.AddMinutes(2); await tracker.PollDueAsync(Ct);
        Assert.Equal(expectedState, (await service.GetAsync(f.Profile.Id, job.Id, Ct))!.Reports[0].State);
        Assert.Equal(remoteStatus == "PARTIALLY" ? "PARTIALLY" : nestedState, (await service.GetAsync(f.Profile.Id, job.Id, Ct))!.Reports[0].ReceiptState);
    }
    [Fact]
    public async Task ServerPreparationSplits30001AndInvalidInputHasNoSideEffects()
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var client = new Client(r => Exchange(r, 200, JsonSerializer.Serialize(new { omsId = SuzDocumentStatusHandler.MockOmsId, reportId = Guid.NewGuid() })));
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var rows = Enumerable.Range(1, 30001).Select(i => new SuzUtilisationRow($"010464001674838921{i:D6}\u001d93dGVz", new Dictionary<string,string>())).ToArray();
        var request = new SuzUtilisationRequest("milk", "GROUP", "UTILISATION", new Dictionary<string,string>(), rows);
        var details = await service.SubmitAsync(f.Profile.Id, request, Ct);
        Assert.Equal(30001, details.Job.CodeCount);
        Assert.Equal(new[] {30000, 1}, details.Reports.Select(x => x.CodeCount));
        Assert.All(details.Reports, r => Assert.Equal("Tracking", r.State));
        Assert.Equal(2, client.Requests.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(f.Profile.Id, request with { Rows = [new("INVALID", new Dictionary<string,string>())] }, Ct));
        Assert.Single((await service.ListPageAsync(f.Profile.Id, cancellationToken: Ct)).Items);
        Assert.Equal(2, client.Requests.Count);
    }
    [Theory]
    [InlineData(false, "Rejected")]
    [InlineData(true, "Prepared")]
    public async Task SignatureFailureOrCancellationBeforeDispatchNeverBecomesUnknown(bool cancelled, string expectedState)
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        f.Profile.SuzOmsId = Guid.NewGuid().ToString(); f.Profile.SuzConnectionId = "connection_1"; f.Profile.CertificateThumbprint = "thumb";
        await f.Profiles.UpsertAsync(f.Profile, Ct);
        var job = f.Job(); job.Mode = CrptIntegrationMode.Production; job.OmsId = f.Profile.SuzOmsId; job.ConnectionId = f.Profile.SuzConnectionId; job.CertificateThumbprint = "thumb";
        await f.Repository.AddAsync(job, [new(new() { JobId = job.Id, Number = 1, CodeCount = 1 }, "{}")], Ct);
        using var cancel = new CancellationTokenSource();
        var signer = new Signer { OnSign = () => { if (cancelled) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); } throw new InvalidOperationException("private-error-detail"); } };
        var client = new Client(r => throw new InvalidOperationException("Must never dispatch"));
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, signer, f.Logs, () => CrptIntegrationMode.Production, TimeProvider.System);
        var result = await service.ContinueAsync(f.Profile.Id, job.Id, cancel.Token);
        Assert.Equal(expectedState, result.Reports[0].State); Assert.Empty(client.Requests);
        Assert.DoesNotContain("private-error-detail", result.Reports[0].Error ?? "");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackingPausesWithoutNetworkAtDeadlineOrChangedModeAndManualResumeRenewsWindow(bool changedMode)
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var clock = new Clock(); var mode = CrptIntegrationMode.Mock; var job = f.Job();
        var report = new SuzUtilisationReport { JobId = job.Id, Number = 1, State = "Tracking", ReportId = Guid.NewGuid().ToString(),
            SubmittedAtUnixMs = clock.Now.ToUnixTimeMilliseconds(), NextCheckAtUnixMs = 0,
            TrackingUntilUnixMs = clock.Now.AddHours(24).ToUnixTimeMilliseconds() };
        await f.Repository.AddAsync(job, [new(report, "{}")], Ct);
        var client = new Client(_ => throw new InvalidOperationException("No network while paused"));
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => mode, clock);
        var tracker = new SuzUtilisationTracker(f.Repository, f.Profiles, client, service, () => mode, clock);
        if (changedMode) mode = CrptIntegrationMode.Production; else clock.Now = clock.Now.AddHours(24);
        await tracker.PollDueAsync(Ct);
        mode = CrptIntegrationMode.Mock;
        var paused = (await service.GetAsync(f.Profile.Id, job.Id, Ct))!.Reports[0];
        Assert.Equal("Paused", paused.State); Assert.Null(paused.NextCheckAtUnixMs); Assert.Empty(client.Requests);
        await service.ResumeAsync(f.Profile.Id, report.Id, Ct);
        var resumed = (await service.GetAsync(f.Profile.Id, job.Id, Ct))!.Reports[0];
        Assert.Equal("Tracking", resumed.State);
        Assert.Equal(clock.Now.AddHours(24).ToUnixTimeMilliseconds(), resumed.TrackingUntilUnixMs);
        Assert.Equal(clock.Now.ToUnixTimeMilliseconds(), resumed.NextCheckAtUnixMs);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData(401, "Rejected")]
    [InlineData(408, "Unknown")]
    [InlineData(503, "Unknown")]
    [InlineData(0, "Unknown")]
    public async Task FailedOrLostCreationResponseStopsFollowingPartsWithoutRetry(int status, string state)
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var job = f.Job();
        await f.Repository.AddAsync(job, Enumerable.Range(1, 2).Select(i => new SuzUtilisationDraft(new() { JobId = job.Id, Number = i, CodeCount = 1 }, "{}")).ToArray(), Ct);
        var client = new Client(r => status == 0 ? throw new IOException("lost-response-private-detail") : Exchange(r, status, "{}"));
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var details = await service.ContinueAsync(f.Profile.Id, job.Id, Ct);
        Assert.Equal(state, details.Reports[0].State); Assert.Equal("Prepared", details.Reports[1].State);
        Assert.Single(client.Requests);
        Assert.DoesNotContain("private-detail", details.Reports[0].Error ?? "");
    }

    [Fact]
    public async Task IndependentReportsOverlapWithBoundedRequestsAndDurableGuids()
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var job = f.Job();
        await f.Repository.AddAsync(job, Enumerable.Range(1, 5).Select(i => new SuzUtilisationDraft(
            new() { JobId = job.Id, Number = i, CodeCount = 1 }, JsonSerializer.Serialize(new { sntins = new[] { "unique-" + i } }))).ToArray(), Ct);
        var entered = 0;
        var active = 0;
        var peak = 0;
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(r => Exchange(r, 200, JsonSerializer.Serialize(new { omsId = SuzDocumentStatusHandler.MockOmsId, reportId = Guid.NewGuid() })))
        {
            BeforeResponse = async () =>
            {
                var current = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref peak, Math.Max(Volatile.Read(ref peak), current));
                if (Interlocked.Increment(ref entered) == 4) firstWave.SetResult();
                await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                Interlocked.Decrement(ref active);
            }
        };
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var saved = await service.ContinueAsync(f.Profile.Id, job.Id, Ct);
        Assert.Equal(4, peak);
        Assert.Equal(5, entered);
        Assert.All(saved.Reports, report => { Assert.Equal("Tracking", report.State); Assert.NotNull(report.ReportId); });
    }

    [Fact]
    public async Task SharedCodesKeepReportsSequential()
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var job = f.Job();
        await f.Repository.AddAsync(job, Enumerable.Range(1, 2).Select(i => new SuzUtilisationDraft(
            new() { JobId = job.Id, Number = i, CodeCount = 1 }, "{\"sntins\":[\"same-code\"]}")).ToArray(), Ct);
        var active = 0;
        var order = new List<int>();
        var client = new Client(r => Exchange(r, 200, JsonSerializer.Serialize(new { omsId = SuzDocumentStatusHandler.MockOmsId, reportId = Guid.NewGuid() })))
        {
            BeforeResponse = async () =>
            {
                Assert.Equal(1, Interlocked.Increment(ref active));
                order.Add(order.Count + 1);
                await Task.Yield();
                Interlocked.Decrement(ref active);
            }
        };
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var saved = await service.ContinueAsync(f.Profile.Id, job.Id, Ct);
        Assert.Equal(2, order.Count);
        Assert.All(saved.Reports, report => Assert.Equal("Tracking", report.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeTransportDispatchKeepsReportPrepared(bool wrapped)
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var job = f.Job();
        await f.Repository.AddAsync(job, [new(new() { JobId = job.Id, Number = 1, CodeCount = 1 }, "{}")], Ct);
        var client = new Client(_ => throw (wrapped
            ? new TaskCanceledException("HttpClient cancellation", new ApiDispatchNotStartedException(new OperationCanceledException()))
            : new ApiDispatchNotStartedException(new OperationCanceledException())));
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new Signer(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var saved = await service.ContinueAsync(f.Profile.Id, job.Id, Ct);
        Assert.Equal("Prepared", Assert.Single(saved.Reports).State);
        Assert.Null(saved.Reports[0].ReportId);
    }

    private static IntegrationExchange Exchange(IntegrationRequest request, int status, string body) => new(status, request.Method, request.Url, request.Headers, request.Body, new Dictionary<string,string>(), body, TimeSpan.Zero);
    private sealed class Client(Func<IntegrationRequest, IntegrationExchange> handler) : ISuzClient
    {
        public Func<IntegrationRequest, IntegrationExchange> Handler { get; set; } = handler;
        public List<IntegrationRequest> Requests { get; } = [];
        public Func<Task>? BeforeResponse { get; init; }
        public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.BeforeDispatchAsync is not null) await request.BeforeDispatchAsync(cancellationToken);
            lock (Requests) Requests.Add(request);
            if (BeforeResponse is not null) await BeforeResponse();
            return Handler(request);
        }
    }
    private sealed class Signer : ICertificateSignatureService
    {
        public string? Json { get; private set; }
        public Action? OnSign { get; init; }
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SignDetachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) { OnSign?.Invoke(); Json = data; return Task.FromResult("signature"); }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-02T12:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
}






