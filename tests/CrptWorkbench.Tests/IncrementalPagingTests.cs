using System.Text;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class IncrementalPagingTests
{
    [Fact]
    public async Task TraceXCleanupCanRetireAnImportWhileAnOldReaderFinishes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"crpt-import-cleanup-{Guid.NewGuid():N}");
        var store = new TraceXImportStore(directory);
        var path = store.CreatePath();
        try
        {
            await using (var output = store.OpenWrite(path))
                await output.WriteAsync(Encoding.UTF8.GetBytes("Старый результат\n"), TestContext.Current.CancellationToken);
            await using (var source = store.OpenRead(path))
            {
                store.Delete(path);
                using var reader = new StreamReader(source, leaveOpen: true);
                Assert.Equal("Старый результат\n", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
            }
            Assert.False(File.Exists(path));
        }
        finally { store.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(200)]
    [InlineData(250)]
    public async Task SqlitePagesReachEveryRecordWithEqualTimestamps(int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"crpt-pages-{Guid.NewGuid():N}.db");
        var factory = new TraceXAndFavoritesTests.TestDbFactory(new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        var profile = new CrptProfile { Name = "Paging" };
        var other = new CrptProfile { Name = "Other" };
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync(ct);
                db.Profiles.AddRange(profile, other);
                for (var i = 0; i < count; i++)
                {
                    db.ApiRequestLogs.Add(new() { ProfileId = profile.Id, CreatedAtUnixMs = 100 });
                    db.BulkCodeJobs.Add(new() { ProfileId = profile.Id, CreatedAtUnixMs = 100 });
                    foreach (var kind in Enum.GetValues<PackageOperationKind>())
                        db.PackageDocuments.Add(new() { ProfileId = profile.Id, OperationKind = kind, CreatedAtUnixMs = 100 });
                }
                db.ApiRequestLogs.Add(new() { ProfileId = other.Id, CreatedAtUnixMs = 200 });
                await db.SaveChangesAsync(ct);
            }
            var history = new ApiRequestLogRepository(factory);
            var jobs = new BulkCodeJobRepository(factory);
            var documents = new PackageDocumentRepository(factory);
            await VerifyPages(cursor => history.ListPageAsync(profile.Id, cursor, ct), x => x.Id, count);
            await VerifyPages(cursor => jobs.ListPageAsync(profile.Id, cursor, ct), x => x.Id, count);
            foreach (var kind in Enum.GetValues<PackageOperationKind>())
                await VerifyPages(cursor => documents.ListPageAsync(profile.Id, kind, cursor, ct), x => x.Id, count);
            var first = await history.ListPageAsync(profile.Id, cancellationToken: ct);
            await using (var db = factory.CreateDbContext())
            {
                db.ApiRequestLogs.Add(new() { ProfileId = profile.Id, CreatedAtUnixMs = 300 });
                await db.SaveChangesAsync(ct);
            }
            if (first.NextCursor is not null)
                Assert.All((await history.ListPageAsync(profile.Id, first.NextCursor, ct)).Items,
                    x => Assert.Equal(100, x.CreatedAtUnixMs));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static async Task VerifyPages<T>(Func<RecordPageCursor?, Task<RecordPage<T>>> read, Func<T, Guid> id, int count)
    {
        var ids = new List<Guid>();
        RecordPageCursor? cursor = null;
        do
        {
            var page = await read(cursor);
            Assert.Equal((long)count, page.TotalCount);
            Assert.InRange(page.Items.Count, 0, 100);
            ids.AddRange(page.Items.Select(id));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(count, ids.Count);
        Assert.Equal(count, ids.Distinct().Count());
    }

    [Fact]
    public async Task FilePagesResumeAtExactUnicodeByteOffsetBeyondOneThousand()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"crpt-file-pages-{Guid.NewGuid():N}");
        var store = new BulkCodeFileStore(directory);
        var id = Guid.NewGuid();
        try
        {
            var expected = Enumerable.Range(0, 1250).Select(i => $"Код-{i}").ToArray();
            await store.WriteFragmentAsync(id, 0, expected.Take(900).Select(x => ("APPLIED", x)).ToArray(), 900, 0, ct);
            await store.WriteFragmentAsync(id, 1, expected.Skip(900).Select(x => ("APPLIED", x)).ToArray(), 350, 0, ct);
            var statuses = new HashSet<string> { "APPLIED" };
            var actual = new List<string>();
            FilePageCursor? cursor = null;
            do
            {
                var page = await store.ReadPageAsync(id, 2, statuses, cursor, ct);
                Assert.InRange(page.Items.Count, 1, 100);
                actual.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(expected, actual);
            var exported = new List<string>();
            await foreach (var code in store.ReadCodesAsync(id, 2, statuses, ct)) exported.Add(code);
            Assert.Equal(expected, exported);
            Assert.Empty((await store.ReadPageAsync(id, 2, new HashSet<string> { "INTRODUCED" }, cancellationToken: ct)).Items);
        }
        finally { store.Delete(id); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PositionReaderDoesNotConfuseReadAheadWithConsumedPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes("Код\r\nСледующий\nПоследний");
        await using var stream = new MemoryStream(bytes);
        var reader = new Utf8PositionReader(stream);
        Assert.Equal("Код", await reader.ReadLineAsync(ct));
        var position = reader.Position;
        Assert.Equal(Encoding.UTF8.GetByteCount("Код\r\n"), position);
        stream.Position = position;
        var resumed = new Utf8PositionReader(stream, position);
        Assert.Equal("Следующий", await resumed.ReadLineAsync(ct));
        Assert.Equal("Последний", await resumed.ReadLineAsync(ct));
        Assert.Null(await resumed.ReadLineAsync(ct));
        Assert.Equal(bytes.Length, resumed.Position);
    }

    [Fact]
    public async Task TraceXPreviewIsProfileScopedAndReachesTheFinalPartialPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"crpt-tracex-pages-{Guid.NewGuid():N}");
        var store = new TraceXImportStore(directory);
        var path = store.CreatePath();
        var profileId = Guid.NewGuid();
        try
        {
            var expected = Enumerable.Range(0, 250).Select(i => $"Упаковка-{i}\tКод-{i}").ToArray();
            await using (var stream = store.OpenWrite(path))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                foreach (var line in expected) await writer.WriteLineAsync(line.AsMemory(), ct);
            var result = new TraceXImportResult(profileId, path, 250, new FileInfo(path).Length, expected.Take(100).ToArray(), 2);
            var service = new TraceXImportService(null!, null!, null!, store);
            var first = await service.PreviewPageAsync(result, profileId, cancellationToken: ct);
            var second = await service.PreviewPageAsync(result, profileId, first.NextCursor, ct);
            var third = await service.PreviewPageAsync(result, profileId, second.NextCursor, ct);
            Assert.Equal(100, first.Items.Count);
            Assert.Equal(100, second.Items.Count);
            Assert.Equal(50, third.Items.Count);
            Assert.Null(third.NextCursor);
            Assert.Equal(expected, first.Items.Concat(second.Items).Concat(third.Items));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewPageAsync(result, Guid.NewGuid(), first.NextCursor, ct));
        }
        finally { store.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
