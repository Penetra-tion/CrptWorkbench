using System.Text;
using System.Text.Json;
using System.Reflection;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class AggregationJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Code(int n) => AggregationTreeTests.Code(n);
    private static CodeInfoItem Node(string code, string? parent = null, string[]? children = null, string type = "UNIT") =>
        new(new CisInfo { RequestedCis = code, Cis = code, Parent = parent, Child = children ?? [], GeneralPackageType = type, Status = "INTRODUCED" });
    private static string[] Codes(IntegrationRequest request) => JsonSerializer.Deserialize<string[]>(request.Body!)!;
    private static AggregationTreeTests.FakeClient Graph(params CodeInfoItem[] items)
    {
        var map = items.ToDictionary(x => x.CisInfo!.RequestedCis!);
        return new((r, _) => Task.FromResult(AggregationTreeTests.Response(r, Codes(r).Where(map.ContainsKey).Select(c => map[c]))));
    }

    [Fact]
    public async Task PollingRefreshesActiveJobsTogetherWithoutReadingCompletedJobs()
    {
        await using var f = await Fixture.CreateAsync();
        var running = await f.PrepareAsync(Code(1));
        var queued = await f.PrepareAsync(Code(2));
        var completed = await f.PrepareAsync(Code(3));
        await f.Repository.TransitionAsync(f.Profile.Id, running.Id, CrptIntegrationMode.Mock, ["Ready"], "Running", null, Ct);
        await f.Repository.TransitionAsync(f.Profile.Id, queued.Id, CrptIntegrationMode.Mock, ["Ready"], "Queued", null, Ct);
        await f.Repository.TransitionAsync(f.Profile.Id, completed.Id, CrptIntegrationMode.Mock, ["Ready"], "Completed", null, Ct);
        var loaded = (await f.Service.ListAsync(f.Profile.Id, cancellationToken: Ct)).ToList();
        var proxy = DispatchProxy.Create<IAggregationTreeRepository, CountingRepository>();
        var counts = (CountingRepository)(object)proxy;
        counts.Target = f.Repository;
        var component = new CrptWorkbench.Web.Components.AggregationTreeJobs();
        var type = component.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        type.GetProperty("Jobs", flags)!.SetValue(component, new AggregationTreeJobService(proxy, f.Files, f.Profiles, f.Options));
        type.GetProperty("Workbench", flags)!.SetValue(component, new WorkbenchContext { ProfileId = f.Profile.Id });
        var visible = (List<AggregationTreeJob>)type.GetField("_jobs", flags)!.GetValue(component)!;
        visible.AddRange(loaded);
        await f.Repository.TransitionAsync(f.Profile.Id, running.Id, CrptIntegrationMode.Mock, ["Running"], "Completed", null, Ct);
        try
        {
            await (Task)type.GetMethod("RefreshLoadedAsync", flags)!.Invoke(component, null)!;
            Assert.Equal(0, counts.SingleJobReads);
            Assert.Equal(1, counts.GroupJobReads);
            Assert.Equal(new[] { running.Id, queued.Id }.Order(), counts.LastGroupIds.Order());
            Assert.Equal("Completed", visible.Single(x => x.Id == running.Id).State);
            Assert.Equal("Completed", visible.Single(x => x.Id == completed.Id).State);
        }
        finally { await component.DisposeAsync(); }
    }

    [Fact]
    public async Task BatchedRefreshIsProfileAndModeScopedAndHandlesMoreThanFiveHundredIds()
    {
        await using var f = await Fixture.CreateAsync();
        var expected = new List<Guid>();
        for (var i = 0; i < 503; i++)
        {
            var id = Guid.NewGuid(); expected.Add(id);
            await f.Repository.CreateAsync(id, f.Profile.Id, CrptIntegrationMode.Mock, "job", 1, Ct);
        }
        var other = Guid.NewGuid();
        await f.Repository.CreateAsync(other, Guid.NewGuid(), CrptIntegrationMode.Mock, "other-profile", 1, Ct);
        var production = Guid.NewGuid();
        await f.Repository.CreateAsync(production, f.Profile.Id, CrptIntegrationMode.Production, "other-mode", 1, Ct);
        // Invoke through the interface so the default contract is exercised before the optimized implementation exists.
        var method = typeof(IAggregationTreeRepository).GetMethod("GetManyAsync");
        Assert.NotNull(method);
        var result = await (Task<IReadOnlyList<AggregationTreeJob>>)method.Invoke(f.Repository,
            [f.Profile.Id, expected.Append(other).Append(production).Append(Guid.NewGuid()).ToArray(), CrptIntegrationMode.Mock, Ct])!;
        Assert.Equal(expected.Order(), result.Select(x => x.Id).Order());
    }

    [Fact]
    public async Task BatchClaimIsBoundedAndResetPreservesTheOriginalLexicalOrder()
    {
        await using var f = await Fixture.CreateAsync();
        var job = await f.PrepareAsync(Code(1));
        await f.Service.StartAsync(f.Profile.Id, job.Id, false, Ct);
        var codes = Enumerable.Range(1, 2001).Select(Code).Order(StringComparer.Ordinal).ToArray();
        await f.Repository.ImportAsync(job.Id, job.SourceBytes,
            codes.Select((code, index) => new AggregationSeed(index + 1, code)).ToArray(), Ct);
        var first = (await f.Repository.TakeBatchAsync(job.Id, Ct))!;
        var second = (await f.Repository.TakeBatchAsync(job.Id, Ct))!;
        Assert.Equal(codes.Take(1000), first.Codes);
        Assert.Equal(codes.Skip(1000).Take(1000), second.Codes);
        await f.Repository.ResetBatchAsync(job.Id, first, Ct);
        Assert.Equal(first.Codes, (await f.Repository.TakeBatchAsync(job.Id, Ct))!.Codes);
        Assert.Equal(codes.Skip(2000), (await f.Repository.TakeBatchAsync(job.Id, Ct))!.Codes);
        Assert.Null(await f.Repository.TakeBatchAsync(job.Id, Ct));
    }

    [Fact]
    public async Task OpeningOldHistoryJobDoesNotSkipInterveningJobPages()
    {
        await using var f = await Fixture.CreateAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 201; i++)
        {
            var id = Guid.NewGuid(); ids.Add(id);
            await f.Repository.CreateAsync(id, f.Profile.Id, CrptIntegrationMode.Mock, $"job-{i}", 1, Ct);
        }
        var component = new CrptWorkbench.Web.Components.AggregationTreeJobs();
        var type = component.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        type.GetProperty("OpenJobId", flags)!.SetValue(component, ids[0]);
        type.GetProperty("Jobs", flags)!.SetValue(component, f.Service);
        type.GetProperty("Workbench", flags)!.SetValue(component, new WorkbenchContext { ProfileId = f.Profile.Id });
        async Task Call(string name) => await (Task)type.GetMethod(name, flags)!.Invoke(component, null)!;
        try
        {
            await Call("RefreshAsync");
            await Call("OnParametersSetAsync");
            var visible = (List<AggregationTreeJob>)type.GetField("_jobs", flags)!.GetValue(component)!;
            Assert.Equal(101, visible.Count); // The newest 100 plus one old job opened from history.
            await Call("LoadMorePageAsync");
            Assert.Equal(201, visible.Count);
            Assert.Equal(ids.Order(), visible.Select(j => j.Id).Order());
        }
        finally { await component.DisposeAsync(); }
    }

    [Fact]
    public async Task ResponseMetadataIsStagedInBoundedTransactionsRatherThanPerNodeCommits()
    {
        await using var f = await Fixture.CreateAsync();
        var codes = Enumerable.Range(1, 128).Select(Code).ToArray();
        var proxy = DispatchProxy.Create<IAggregationTreeRepository, CountingRepository>();
        var counts = (CountingRepository)(object)proxy;
        counts.Target = f.Repository;
        var job = await f.PrepareAsync(string.Join('\n', codes));
        await f.Service.StartAsync(f.Profile.Id, job.Id, false, Ct);
        await f.Processor(Graph(codes.Select(c => Node(c)).ToArray()), proxy).ProcessNextAsync(Ct);
        Assert.Equal(codes.Length, (await f.GetAsync(job)).NodesLoaded);
        Assert.Equal(0, counts.SingleNodeWrites);
        Assert.Equal(1, counts.BatchNodeWrites);
    }

    [Fact]
    public async Task ResumeRequeuesClaimedButNeverDispatchedBatch()
    {
        await using var f = await Fixture.CreateAsync();
        var job = await f.PrepareAsync(Code(1) + "\n" + Code(2));
        await f.Service.StartAsync(f.Profile.Id, job.Id, false, Ct);
        await f.Repository.TransitionAsync(f.Profile.Id, job.Id, CrptIntegrationMode.Mock, ["Queued"], "Running", null, Ct);
        await f.Repository.ImportAsync(job.Id, job.SourceBytes, [new(1, Code(1)), new(2, Code(2))], Ct);
        Assert.Equal(2, (await f.Repository.TakeBatchAsync(job.Id, Ct))!.Codes.Count);
        // A later claim/storage failure can stop the worker before it dispatches this persisted claim.
        await f.Repository.TransitionAsync(f.Profile.Id, job.Id, CrptIntegrationMode.Mock, ["Running"], "Paused", "Ошибка диска", Ct);
        var client = Graph(Node(Code(1)), Node(Code(2)));
        await f.Service.ResumeAsync(f.Profile.Id, job.Id, Ct);
        await f.Processor(client).ProcessNextAsync(Ct);
        var completed = await f.GetAsync(job);
        Assert.Equal("Completed", completed.State);
        Assert.Equal(2, completed.NodesLoaded);
        Assert.Equal(2, client.Requests.SelectMany(Codes).Distinct().Count());
    }

    [Fact]
    public async Task SeedDescendantUpgradeDoesNotSkipCachedAncestorsChildren()
    {
        await using var f = await Fixture.CreateAsync();
        var client = Graph(Node(Code(1), children: [Code(2)], type: "BOX"),
            Node(Code(2), Code(1), [Code(3)], "BOX"), Node(Code(3), Code(2), [Code(4), Code(5)], "GROUP"),
            Node(Code(4), Code(3)), Node(Code(5), Code(3)));
        var job = await f.RunAsync(client, Code(1) + "\n" + Code(4));
        Assert.Equal(5, (await f.GetAsync(job)).NodesLoaded);
        Assert.Equal(5, client.Requests.SelectMany(Codes).Distinct().Count());
        var exported = await f.ExportAsync(job, new());
        Assert.Equal(Encoding.UTF8.GetBytes($"{Code(4)}\t{Code(3)}\t{Code(2)}\t{Code(1)}\r\n{Code(5)}\t{Code(3)}\t{Code(2)}\t{Code(1)}\r\n"), exported.Bytes);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 6)]
    public async Task OverlappingSeedsShareAncestorsAndSiblingsAreExplicit(bool siblings, int count)
    {
        await using var f = await Fixture.CreateAsync();
        var client = Graph(Node(Code(1), children: [Code(2), Code(5)], type: "BOX"),
            Node(Code(2), Code(1), [Code(3)], "GROUP"), Node(Code(3), Code(2), [Code(4)], "SET"),
            Node(Code(4), Code(3)), Node(Code(5), Code(1), [Code(6)], "BOX"), Node(Code(6), Code(5)));
        var job = await f.PrepareAsync(Code(3) + "\r\n" + Code(4) + "\r\n" + Code(3));
        Assert.Equal("Ready", job.State);
        Assert.Empty(client.Requests);
        Assert.True(await f.Service.StartAsync(f.Profile.Id, job.Id, siblings, Ct));
        await f.Processor(client).ProcessNextAsync(Ct);
        var saved = await f.GetAsync(job);
        Assert.Equal("Completed", saved.State);
        Assert.Equal(3, saved.LinesRead);
        Assert.Equal(count, saved.NodesTotal);
        Assert.Equal(count, saved.NodesLoaded);
        var requested = client.Requests.SelectMany(Codes).ToArray();
        Assert.Equal(count, requested.Length);
        Assert.Equal(count, requested.Distinct().Count());
        Assert.Equal(siblings, requested.Contains(Code(5)));
        Assert.All(client.Requests, r => Assert.InRange(Codes(r).Length, 1, 1000));
    }

    [Fact]
    public async Task ExportBytesPreserveRepeatedTypesAndDeduplicateProjectedPaths()
    {
        await using var f = await Fixture.CreateAsync();
        var client = Graph(Node(Code(1), children: [Code(2)], type: "BOX"),
            Node(Code(2), Code(1), [Code(3)], "BOX"), Node(Code(3), Code(2), [Code(4), Code(5)], "GROUP"),
            Node(Code(4), Code(3)), Node(Code(5), Code(3)));
        var job = await f.RunAsync(client, Code(1));
        var full = await f.ExportAsync(job, new());
        Assert.Equal(Encoding.UTF8.GetBytes($"{Code(4)}\t{Code(3)}\t{Code(2)}\t{Code(1)}\r\n{Code(5)}\t{Code(3)}\t{Code(2)}\t{Code(1)}\r\n"), full.Bytes);
        Assert.Equal(2, full.File.Lines);
        var boxes = await f.ExportAsync(job, new(false, ["BOX"]));
        Assert.Equal(Encoding.UTF8.GetBytes($"{Code(2)}\t{Code(1)}\r\n"), boxes.Bytes);
        Assert.Equal(1, boxes.File.Lines);
        var groups = await f.ExportAsync(job, new(false, ["GROUP"]));
        Assert.Equal(Encoding.UTF8.GetBytes(Code(3) + "\r\n"), groups.Bytes);
        Assert.Equal(new[] { "BOX", "GROUP", "UNIT" }, (await f.Service.TypesAsync(f.Profile.Id, job.Id, Ct)).Order().ToArray());
    }

    [Fact]
    public async Task UnknownTypeCanBeSelectedAndEmptyTypeSelectionIsRejectedWithoutNetwork()
    {
        await using var f = await Fixture.CreateAsync();
        var client = Graph(Node(Code(1), type: ""));
        var job = await f.RunAsync(client, Code(1));
        Assert.Equal(new[] { "" }, await f.Service.TypesAsync(f.Profile.Id, job.Id, Ct));
        var exported = await f.ExportAsync(job, new(false, [""]));
        Assert.Equal(Encoding.UTF8.GetBytes(Code(1) + "\r\n"), exported.Bytes);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ExportAsync(f.Profile.Id, job.Id, new(false, []), Ct));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task InvalidRowsKeepOriginalNumbersAndValidSerialParenthesesAreNormalizedOnce()
    {
        await using var f = await Fixture.CreateAsync();
        const string serial = "010460000000000021SER(01)X(21)Y";
        var rows = new[] { "(01)04600000000000(21)BAD", serial + "\u001D91SECRET" }
            .Concat(Enumerable.Range(1, 249).Select(i => "invalid-" + i)).Append(Code(2));
        var client = Graph(Node(serial), Node(Code(2)));
        var job = await f.RunAsync(client, string.Join("\r\n", rows));
        var saved = await f.GetAsync(job);
        Assert.Equal("Partial", saved.State);
        Assert.Equal(252, saved.LinesRead);
        Assert.Equal(250, saved.RejectedRows);
        Assert.Equal(2, saved.NodesLoaded);
        Assert.Equal(new[] { serial, Code(2) }.Order(), client.Requests.SelectMany(Codes).Order());
        var rejected = new List<AggregationRejectedRow>();
        long? cursor = null;
        do
        {
            var page = await f.Service.RejectedAsync(f.Profile.Id, job.Id, cursor, Ct);
            Assert.InRange(page.Items.Count, 1, 100);
            rejected.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(new long[] { 1 }.Concat(Enumerable.Range(3, 249).Select(i => (long)i)), rejected.Select(x => x.Line));
        Assert.All(rejected, x => Assert.False(string.IsNullOrWhiteSpace(x.Reason)));
        Assert.Equal("(01)04600000000000(21)BAD", rejected[0].Value);
        Assert.Equal(2, (await f.ExportAsync(job, new())).File.Lines);
    }

    [Fact]
    public async Task MalformedUtf8RowDoesNotDiscardFollowingValidRows()
    {
        await using var f = await Fixture.CreateAsync();
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Code(1) + "\r\n", 999))).Concat(new byte[] { 0xC3, 0x28, 13, 10 })
            .Concat(Encoding.UTF8.GetBytes(Code(2) + "\r\n")).ToArray();
        using var input = new MemoryStream(bytes);
        var job = await f.Service.PrepareAsync(f.Profile.Id, "broken.txt", input, Ct);
        Assert.True(await f.Service.StartAsync(f.Profile.Id, job.Id, false, Ct));
        var client = Graph(Node(Code(1)), Node(Code(2)));
        await f.Processor(client).ProcessNextAsync(Ct);
        var saved = await f.GetAsync(job);
        Assert.Equal("Partial", saved.State);
        Assert.Equal(1001, saved.LinesRead);
        Assert.Equal(bytes.Length, saved.SourceOffset);
        Assert.Equal(saved.SourceBytes, saved.SourceOffset);
        Assert.Equal(1, saved.RejectedRows);
        Assert.Equal(2, saved.NodesLoaded);
        Assert.Equal(1000, Assert.Single((await f.Service.RejectedAsync(f.Profile.Id, job.Id, null, Ct)).Items).Line);
        Assert.Equal(2, client.Requests.SelectMany(Codes).Count());
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("conflicting-parent")]
    [InlineData("missing")]
    public async Task InvalidBranchesDoNotSuppressIndependentCompletePaths(string problem)
    {
        await using var f = await Fixture.CreateAsync();
        var items = new List<CodeInfoItem> { Node(Code(1), children: [Code(2)], type: "BOX"), Node(Code(2), Code(1)) };
        if (problem == "cycle")
            items.AddRange([Node(Code(3), Code(4), [Code(4)], "BOX"), Node(Code(4), Code(3), [Code(3)], "BOX")]);
        if (problem == "conflicting-parent")
            items.AddRange([Node(Code(3), children: [Code(4), Code(5)], type: "BOX"), Node(Code(4), Code(3), [Code(6)], "GROUP"),
                Node(Code(5), Code(3), [Code(6)], "GROUP"), Node(Code(6), Code(4))]);
        var client = Graph(items.ToArray());
        var job = await f.RunAsync(client, Code(1) + "\n" + Code(3));
        Assert.Equal("Partial", (await f.GetAsync(job)).State);
        var exported = await f.ExportAsync(job, new());
        Assert.Equal(Encoding.UTF8.GetBytes(Code(2) + "\t" + Code(1) + "\r\n"), exported.Bytes);
        Assert.Equal(1, exported.File.Lines);
        var requested = client.Requests.SelectMany(Codes).ToArray();
        Assert.Equal(requested.Length, requested.Distinct().Count());
    }

    [Fact]
    public async Task PauseDrainsResponseAndResumeAfterReopenDoesNotRefetchPublishedNodes()
    {
        await using var f = await Fixture.CreateAsync();
        var job = await f.PrepareAsync(Code(1));
        var client = new AggregationTreeTests.FakeClient(async (r, _) =>
        {
            var code = Assert.Single(Codes(r));
            if (code == Code(1))
            {
                await f.Service.PauseAsync(f.Profile.Id, job.Id, Ct);
                return AggregationTreeTests.Response(r, [Node(Code(1), children: [Code(2)], type: "BOX")]);
            }
            return AggregationTreeTests.Response(r, [Node(Code(2), Code(1))]);
        });
        Assert.True(await f.Service.StartAsync(f.Profile.Id, job.Id, false, Ct));
        await f.Processor(client).ProcessNextAsync(Ct);
        var paused = await f.GetAsync(job);
        Assert.Equal("Paused", paused.State);
        Assert.Equal(1, paused.NodesLoaded);
        Assert.Single(client.Requests);
        var repository = new AggregationTreeRepository(f.Factory, new AggregationTreeSourceStore(f.DirectoryPath));
        var reopened = new AggregationTreeJobService(repository, f.Files, f.Profiles, f.Options);
        var processor = f.Processor(client, repository);
        await processor.RecoverAsync(Ct);
        Assert.Equal("Paused", (await reopened.GetAsync(f.Profile.Id, job.Id, Ct))!.State);
        await reopened.ResumeAsync(f.Profile.Id, job.Id, Ct);
        await processor.ProcessNextAsync(Ct);
        var completed = await reopened.GetAsync(f.Profile.Id, job.Id, Ct);
        Assert.Equal("Completed", completed!.State);
        Assert.Equal(1, completed.LinesRead);
        Assert.Equal(2, completed.NodesTotal);
        Assert.Equal(2, completed.NodesLoaded);
        Assert.Equal(new[] { Code(1), Code(2) }, client.Requests.SelectMany(Codes).Order().ToArray());
    }

    [Fact]
    public async Task PauseAfter429PreventsRetryAndNextJobCanRun()
    {
        await using var f = await Fixture.CreateAsync();
        var job = await f.PrepareAsync(Code(1));
        var client = new AggregationTreeTests.FakeClient(async (r, _) =>
        {
            if (Codes(r).Contains(Code(1)))
            {
                await f.Service.PauseAsync(f.Profile.Id, job.Id, Ct);
                return AggregationTreeTests.Response(r, []) with { StatusCode = 429 };
            }
            return AggregationTreeTests.Response(r, [Node(Code(2))]);
        });
        Assert.True(await f.Service.StartAsync(f.Profile.Id, job.Id, false, Ct));
        await f.Processor(client).ProcessNextAsync(Ct);
        Assert.Equal("Paused", (await f.GetAsync(job)).State);
        Assert.Single(client.Requests);
        var next = await f.RunAsync(client, Code(2));
        Assert.Equal("Completed", (await f.GetAsync(next)).State);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task UnrequestedPredispatchCancellationPausesGraphInsteadOfResubmitting()
    {
        await using var f = await Fixture.CreateAsync();
        var attempts = 0;
        var client = new AggregationTreeTests.FakeClient((request, _) =>
            Interlocked.Increment(ref attempts) == 1
                ? throw new ApiDispatchNotStartedException(new OperationCanceledException("Simulated predispatch timeout"))
                : Task.FromResult(AggregationTreeTests.Response(request, [Node(Code(1))])));
        var job = await f.RunAsync(client, Code(1));
        var paused = await f.GetAsync(job);
        Assert.Equal("Paused", paused.State);
        Assert.Equal(0, paused.NodesLoaded);
        Assert.Contains("вручную", paused.Error);
        Assert.Single(client.Requests);
        await f.Service.ResumeAsync(f.Profile.Id, job.Id, Ct);
        await f.Processor(client).ProcessNextAsync(Ct);
        Assert.Equal("Completed", (await f.GetAsync(job)).State);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task RestartRecoversClaimedRunningBatchToManualPause()
    {
        await using var f = await Fixture.CreateAsync();
        var job = await f.PrepareAsync(Code(1));
        Assert.True(await f.Service.StartAsync(f.Profile.Id, job.Id, false, Ct));
        Assert.True(await f.Repository.TransitionAsync(f.Profile.Id, job.Id, CrptIntegrationMode.Mock, ["Queued"], "Running", null, Ct));
        await f.Repository.ImportAsync(job.Id, job.SourceBytes, [new(1, Code(1))], Ct);
        Assert.NotNull(await f.Repository.TakeBatchAsync(job.Id, Ct));
        var repository = new AggregationTreeRepository(f.Factory, f.Files);
        var client = Graph(Node(Code(1)));
        var processor = f.Processor(client, repository);
        await processor.RecoverAsync(Ct);
        Assert.Equal("Paused", (await f.GetAsync(job)).State);
        await processor.ProcessNextAsync(Ct);
        Assert.Empty(client.Requests);
        await f.Service.ResumeAsync(f.Profile.Id, job.Id, Ct);
        await processor.ProcessNextAsync(Ct);
        var completed = await f.GetAsync(job);
        Assert.Equal("Completed", completed.State);
        Assert.Equal(1, completed.NodesLoaded);
        Assert.Equal(1, completed.LinesRead);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task ResponseDiskFailurePausesAndManualResumeKeepsImportedSeeds()
    {
        await using var f = await Fixture.CreateAsync();
        var job = await f.PrepareAsync(Code(1));
        Assert.True(await f.Service.StartAsync(f.Profile.Id, job.Id, false, Ct));
        var client = Graph(Node(Code(1)));
        var brokenDisk = new ThrowingResponseStore(f.Files);
        var processor = new AggregationTreeJobProcessor(f.Repository, brokenDisk, f.Profiles, client,
            new ApiRequestLogRepository(f.Factory), f.Options);
        await processor.ProcessNextAsync(Ct);
        var paused = await f.GetAsync(job);
        Assert.Equal("Paused", paused.State);
        Assert.NotNull(paused.Error);
        Assert.Equal(1, paused.LinesRead);
        Assert.Equal(1, paused.NodesTotal);
        Assert.Empty(client.Requests);
        await f.Service.ResumeAsync(f.Profile.Id, job.Id, Ct);
        await f.Processor(client).ProcessNextAsync(Ct);
        var completed = await f.GetAsync(job);
        Assert.Equal("Completed", completed.State);
        Assert.Equal(1, completed.LinesRead);
        Assert.Equal(1, completed.NodesTotal);
        Assert.Equal(1, completed.NodesLoaded);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task JobsAndExportFilesAreIsolatedByProfileAndMode()
    {
        await using var f = await Fixture.CreateAsync();
        var job = await f.RunAsync(Graph(Node(Code(1))), Code(1));
        var export = await f.Service.ExportAsync(f.Profile.Id, job.Id, new(), Ct);
        var other = new CrptProfile { Name = "Other" };
        await f.Profiles.UpsertAsync(other, Ct);
        Assert.Null(await f.Service.GetAsync(other.Id, job.Id, Ct));
        Assert.Empty(await f.Service.ListAsync(other.Id, null, Ct));
        Assert.False(await f.Service.StartAsync(other.Id, job.Id, true, Ct));
        await f.Service.DeleteAsync(other.Id, job.Id, Ct);
        Assert.NotNull(await f.Service.GetAsync(f.Profile.Id, job.Id, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.OpenExportAsync(other.Id, job.Id, export.Id, Ct));
        var production = new AggregationTreeJobService(f.Repository, f.Files, f.Profiles, new CrptIntegrationOptions { Mode = CrptIntegrationMode.Production });
        Assert.Null(await production.GetAsync(f.Profile.Id, job.Id, Ct));
        Assert.Empty(await production.ListAsync(f.Profile.Id, null, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => production.OpenExportAsync(f.Profile.Id, job.Id, export.Id, Ct));
    }

    [Fact]
    public async Task AdditiveMigrationPreservesPopulatedPrecedingSchema()
    {
        await using var f = await Fixture.CreateAsync(migrate: false);
        await using (var db = f.Factory.CreateDbContext())
            await db.GetService<IMigrator>().MigrateAsync("20261010100000_AddBulkShipmentChecks", Ct);
        await f.Profiles.UpsertAsync(f.Profile, Ct);
        var oldJob = new BulkCodeJob { ProfileId = f.Profile.Id, FileName = "retained.txt", State = "Paused", LinesRead = 37 };
        await using (var db = f.Factory.CreateDbContext())
        {
            db.BulkCodeJobs.Add(oldJob);
            await db.SaveChangesAsync(Ct);
            await db.Database.MigrateAsync(Ct);
        }
        var job = await f.RunAsync(Graph(Node(Code(1))), Code(1));
        Assert.Equal("Completed", (await f.GetAsync(job)).State);
        await using var reopened = f.Factory.CreateDbContext();
        var retained = await reopened.BulkCodeJobs.SingleAsync(Ct);
        Assert.Equal(oldJob.Id, retained.Id);
        Assert.Equal("retained.txt", retained.FileName);
        Assert.Equal(37, retained.LinesRead);
        Assert.Equal(f.Profile.Name, (await f.Profiles.GetAsync(f.Profile.Id, Ct))!.Name);
        Assert.Contains("20261027090000_AddAggregationTreeJobs", await reopened.Database.GetAppliedMigrationsAsync(Ct));
        Assert.False(reopened.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task MoreThanHundredThousandSourceRowsAndGraphNodesAreNotTruncated()
    {
        const int count = 100001;
        await using var f = await Fixture.CreateAsync();
        var root = Code(1);
        var children = Enumerable.Range(2, count - 1).Select(Code).ToArray();
        var active = 0;
        var peak = 0;
        var client = new AggregationTreeTests.FakeClient(async (r, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref peak); }
            while (observed < current && Interlocked.CompareExchange(ref peak, current, observed) != observed);
            try
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return AggregationTreeTests.Response(r, Codes(r).Select(c => c == root
                    ? Node(c, children: children, type: "BOX") : Node(c, root)));
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var job = await f.RunAsync(client, string.Join("\n", Enumerable.Range(1, count).Select(Code)));
        var saved = await f.GetAsync(job);
        Assert.Equal("Completed", saved.State);
        Assert.Equal(count, saved.LinesRead);
        Assert.Equal(count, saved.NodesTotal);
        Assert.Equal(count, saved.NodesLoaded);
        Assert.Equal(count, client.Requests.SelectMany(Codes).Distinct().Count());
        Assert.All(client.Requests, r => Assert.InRange(Codes(r).Length, 1, 1000));
        Assert.InRange(peak, 1, 4);
        Assert.Equal(root, Assert.Single((await f.Service.NodesAsync(f.Profile.Id, job.Id, cancellationToken: Ct)).Items).Code);
        var nodes = new HashSet<string>();
        string? cursor = null;
        do
        {
            var page = await f.Service.NodesAsync(f.Profile.Id, job.Id, root, cursor, Ct);
            Assert.InRange(page.Items.Count, 1, 100);
            Assert.All(page.Items, n => Assert.True(nodes.Add(n.Code), "Paged node repeated"));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(count - 1, nodes.Count);
        var export = await f.ExportAsync(job, new());
        Assert.Equal(count - 1, export.File.Lines);
        var lines = Encoding.UTF8.GetString(export.Bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(count - 1, lines.Length);
        Assert.All(lines, line => Assert.EndsWith("\t" + root, line));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "crpt-aggregation-job-" + Guid.NewGuid().ToString("N"));
        public CrptProfile Profile { get; } = new() { Name = "Durable aggregation", Inn = "7700000000" };
        public CrptIntegrationOptions Options { get; } = new() { Mode = CrptIntegrationMode.Mock };
        public DbFactory Factory { get; private set; } = null!;
        public ProfileRepository Profiles { get; private set; } = null!;
        public AggregationTreeSourceStore Files { get; private set; } = null!;
        public AggregationTreeRepository Repository { get; private set; } = null!;
        public AggregationTreeJobService Service { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(bool migrate = true)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.DirectoryPath);
            f.Factory = new(new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite($"Data Source={Path.Combine(f.DirectoryPath, "jobs.db")};Pooling=False").Options);
            f.Profiles = new(f.Factory);
            f.Files = new(f.DirectoryPath);
            f.Repository = new(f.Factory, f.Files);
            f.Service = new(f.Repository, f.Files, f.Profiles, f.Options);
            if (migrate)
            {
                await using var db = f.Factory.CreateDbContext();
                await db.Database.MigrateAsync(Ct);
                await f.Profiles.UpsertAsync(f.Profile, Ct);
            }
            return f;
        }
        public AggregationTreeJobProcessor Processor(ITrueApiClient client, IAggregationTreeRepository? repository = null) =>
            new(repository ?? Repository, Files, Profiles, client, new ApiRequestLogRepository(Factory), Options);
        public async Task<AggregationTreeJob> PrepareAsync(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            return await Service.PrepareAsync(Profile.Id, "codes.txt", stream, Ct);
        }
        public async Task<AggregationTreeJob> RunAsync(ITrueApiClient client, string text)
        {
            var job = await PrepareAsync(text);
            Assert.True(await Service.StartAsync(Profile.Id, job.Id, false, Ct));
            await Processor(client).ProcessNextAsync(Ct);
            return job;
        }
        public async Task<AggregationTreeJob> GetAsync(AggregationTreeJob job) => (await Service.GetAsync(Profile.Id, job.Id, Ct))!;
        public async Task<(AggregationExportFile File, byte[] Bytes)> ExportAsync(AggregationTreeJob job, AggregationExportOptions options)
        {
            var file = await Service.ExportAsync(Profile.Id, job.Id, options, Ct);
            await using var source = await Service.OpenExportAsync(Profile.Id, job.Id, file.Id, Ct);
            using var target = new MemoryStream();
            await source.CopyToAsync(target, Ct);
            return (file, target.ToArray());
        }
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class DbFactory(DbContextOptions<WorkbenchDbContext> options) : IDbContextFactory<WorkbenchDbContext>
    { public WorkbenchDbContext CreateDbContext() => new(options); }

    public class CountingRepository : DispatchProxy
    {
        public IAggregationTreeRepository Target { get; set; } = null!;
        public int SingleNodeWrites, BatchNodeWrites, SingleJobReads, GroupJobReads;
        public IReadOnlyList<Guid> LastGroupIds { get; private set; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "StageNodeAsync") Interlocked.Increment(ref SingleNodeWrites);
            if (targetMethod.Name == "StageNodesAsync") Interlocked.Increment(ref BatchNodeWrites);
            if (targetMethod.Name == "GetAsync") Interlocked.Increment(ref SingleJobReads);
            if (targetMethod.Name == "GetManyAsync")
            {
                Interlocked.Increment(ref GroupJobReads);
                LastGroupIds = (IReadOnlyList<Guid>)args![1]!;
            }
            return targetMethod.Invoke(Target, args);
        }
    }

    private sealed class ThrowingResponseStore(IAggregationTreeSourceStore inner) : IAggregationTreeSourceStore
    {
        public Task<long> SaveAsync(Guid id, Stream source, CancellationToken ct) => inner.SaveAsync(id, source, ct);
        public Stream OpenRead(Guid id, long offset = 0) => inner.OpenRead(id, offset);
        public Stream CreateResponse(Guid id, Guid batch) => throw new IOException("Simulated full disk");
        public void DeleteResponse(Guid id, Guid batch) => inner.DeleteResponse(id, batch);
        public string ExportPath(Guid id, Guid export) => inner.ExportPath(id, export);
        public Stream OpenExport(Guid id, Guid export) => inner.OpenExport(id, export);
        public void CleanupResponses() => inner.CleanupResponses();
        public void Delete(Guid id) => inner.Delete(id);
    }
}
