using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzTrackingConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IndependentReportsOverlapButEachReportChecksStatusBeforeReceiptAndPassStopsAtTen()
    {
        await using var f = await SuzUtilisationPersistenceTests.Fixture.CreateAsync();
        var job = f.Job();
        var reports = Enumerable.Range(1, 12).Select(i => new SuzUtilisationReport
        {
            JobId = job.Id, Number = i, State = "Tracking", ReportId = Guid.NewGuid().ToString(),
            NextCheckAtUnixMs = 0, TrackingUntilUnixMs = long.MaxValue
        }).ToArray();
        await f.Repository.AddAsync(job, reports.Select(report => new SuzUtilisationDraft(report, "{}")).ToArray(), Ct);
        var client = new BlockingClient();
        var service = new SuzUtilisationService(f.Repository, f.Profiles, client, new NoSigner(), f.Logs, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var tracker = new SuzUtilisationTracker(f.Repository, f.Profiles, client, service, () => CrptIntegrationMode.Mock, TimeProvider.System);
        var polling = tracker.PollDueAsync(Ct);
        try { await client.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); }
        finally { client.Release.TrySetResult(); await polling; }
        Assert.Equal(4, client.Maximum);
        Assert.Equal(10, client.StatusCalls);
        Assert.Equal(10, client.ReceiptCalls);
        Assert.Equal(10, (await service.GetAsync(f.Profile.Id, job.Id, Ct))!.Reports.Count(x => x.PollCount == 1));
    }

    private sealed class BlockingClient : ISuzClient
    {
        private int _active, _maximum, _statusCalls, _receiptCalls;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _statusCompleted = new();
        public int Maximum => Volatile.Read(ref _maximum);
        public int StatusCalls => Volatile.Read(ref _statusCalls);
        public int ReceiptCalls => Volatile.Read(ref _receiptCalls);
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
        {
            var query = new Uri("https://suz.mock.invalid/" + request.Url).Query;
            var id = query[(query.LastIndexOf('=') + 1)..];
            string body;
            if (request.OperationId == "suz.report-info")
            {
                Interlocked.Increment(ref _statusCalls);
                var active = Interlocked.Increment(ref _active);
                int previous;
                do { previous = Volatile.Read(ref _maximum); }
                while (active > previous && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
                if (active == 4) FourStarted.TrySetResult();
                try { await Release.Task.WaitAsync(cancellationToken); }
                finally { Interlocked.Decrement(ref _active); }
                _statusCompleted[id] = 0;
                body = JsonSerializer.Serialize(new { omsId = SuzDocumentStatusHandler.MockOmsId, reportId = id, status = "PENDING" });
            }
            else
            {
                Assert.True(_statusCompleted.ContainsKey(id));
                Interlocked.Increment(ref _receiptCalls);
                body = "{\"results\":[]}";
            }
            return new(200, request.Method, request.Url, request.Headers, request.Body, new Dictionary<string, string>(), body, TimeSpan.Zero);
        }
    }

    private sealed class NoSigner : ICertificateSignatureService
    {
        public Task<string> SignAttachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
