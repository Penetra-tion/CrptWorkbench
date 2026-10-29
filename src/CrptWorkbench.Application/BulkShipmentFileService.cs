using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record BulkShipmentSummary(string Inn, CrptIntegrationMode Mode, long ReviewedRows, long TotalRows,
    long UnknownRows, bool Completed, IReadOnlyDictionary<string, long> ShippedCounts);

public sealed class BulkShipmentFileService(IBulkCodeJobRepository jobs, IBulkCodeFileStore files,
    IProfileRepository profiles, IBulkShipmentCheckRepository checks, CrptIntegrationOptions options)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<BulkShipmentCheckScope, OwnershipProgress> _ownership = new();
    private sealed class OwnershipProgress
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long NextBatch { get; set; }
        public HashSet<long> Missing { get; } = [];
        public long Reviewed { get; set; }
        public long Unknown { get; set; }
        public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);
    }

    public async Task<BulkShipmentSummary> GetOwnershipSummaryAsync(Guid profileId, Guid jobId, CancellationToken token = default)
    {
        var (job, scope) = await ResolveOwnershipAsync(profileId, jobId, token);
        var saved = _ownership.GetOrAdd(scope, _ => new());
        await saved.Gate.WaitAsync(token);
        try
        {
            foreach (var number in saved.Missing.ToArray())
                if (await CountOwnedFragmentAsync(jobId, number, scope.Inn, saved, token)) saved.Missing.Remove(number);
            for (var number = saved.NextBatch; number < job.NextBatchNumber; number++)
            {
                if (!await CountOwnedFragmentAsync(jobId, number, scope.Inn, saved, token)) saved.Missing.Add(number);
                saved.NextBatch = number + 1;
            }
            return new(scope.Inn, scope.Mode, saved.Reviewed, job.CodesFound, saved.Unknown,
                job.State == "Completed" && saved.Reviewed == job.CodesFound,
                new Dictionary<string, long>(saved.Counts, StringComparer.Ordinal));
        }
        finally { saved.Gate.Release(); }
    }

    public async Task<FileRecordPage> OwnershipPreviewPageAsync(Guid profileId, Guid jobId, IReadOnlySet<string> statuses,
        FilePageCursor? cursor = null, CancellationToken token = default)
    {
        var (job, scope) = await ResolveOwnershipAsync(profileId, jobId, token);
        var result = new List<string>(100);
        do
        {
            var page = await ReadOwnershipPageAsync(jobId, job.NextBatchNumber, statuses, cursor, token, 100 - result.Count);
            result.AddRange(page.Items.Where(entry => IsOwnedShipment(entry, scope.Inn)).Select(entry => entry.Code));
            cursor = page.NextCursor;
            if (page.Items.Count == 0) cursor = null;
        } while (result.Count < 100 && cursor is not null);
        return new(result, cursor);
    }

    public async IAsyncEnumerable<string> OwnershipExportAsync(Guid profileId, Guid jobId, IReadOnlySet<string> statuses,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default,
        string? expectedInn = null, CrptIntegrationMode? expectedMode = null)
    {
        var (job, scope) = await ResolveOwnershipAsync(profileId, jobId, token);
        if ((expectedInn is not null && scope.Inn != expectedInn) || (expectedMode is not null && scope.Mode != expectedMode))
            throw new InvalidOperationException("Профиль выгрузки изменился.");
        FilePageCursor? cursor = null;
        do
        {
            var page = await ReadOwnershipPageAsync(jobId, job.NextBatchNumber, statuses, cursor, token);
            foreach (var entry in page.Items)
                if (IsOwnedShipment(entry, scope.Inn)) yield return entry.Code;
            cursor = page.NextCursor;
            if (page.Items.Count == 0) yield break;
        } while (cursor is not null);
    }

    private async Task<BulkCodeEntryPage> ReadOwnershipPageAsync(Guid jobId, long count, IReadOnlySet<string> statuses,
        FilePageCursor? cursor, CancellationToken token, int take = 100)
    {
        while (true)
        {
            var page = await files.ReadEntriesPageAsync(jobId, count, statuses, cursor, token, take);
            if (page.Items.Count != 0 || page.NextCursor is null) return page;
            // Ownership selection has no request/checkpoint side effects. Skip missing fragments
            // in this download snapshot; counters revisit them when they are atomically published.
            cursor = new(page.NextCursor.BatchNumber + 1, 0);
        }
    }

    private async Task<bool> CountOwnedFragmentAsync(Guid jobId, long number, string inn, OwnershipProgress saved, CancellationToken token)
    {
        if (!files.FragmentExists(jobId, number)) return false;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long reviewed = 0, unknown = 0;
        FilePageCursor? cursor = new(number, 0);
        do
        {
            var page = await files.ReadEntriesPageAsync(jobId, number + 1, null, cursor, token);
            foreach (var entry in page.Items)
            {
                reviewed++;
                if (!entry.OwnerKnown) unknown++;
                else if (IsOwnedShipment(entry, inn)) counts[entry.Status] = counts.GetValueOrDefault(entry.Status) + 1;
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        // Commit only a completely read immutable fragment, so cancellation never double-counts.
        saved.Reviewed += reviewed;
        saved.Unknown += unknown;
        foreach (var (status, count) in counts) saved.Counts[status] = saved.Counts.GetValueOrDefault(status) + count;
        return true;
    }

    private static bool IsOwnedShipment(BulkCodeEntry entry, string inn) => entry.OwnerKnown &&
        (string.IsNullOrWhiteSpace(entry.OwnerInn) || !string.Equals(entry.OwnerInn.Trim(), inn, StringComparison.Ordinal));

    private async Task<(BulkCodeJob Job, BulkShipmentCheckScope Scope)> ResolveOwnershipAsync(Guid profileId, Guid jobId, CancellationToken token)
    {
        var resolved = await ResolveAsync(profileId, jobId, token);
        if (string.IsNullOrWhiteSpace(resolved.Scope.Inn))
            throw new InvalidOperationException("Для фильтра «Отгружен» укажите ИНН текущего профиля в настройках.");
        return (resolved.Job, resolved.Scope with { Inn = resolved.Scope.Inn.Trim() });
    }

    public async Task<BulkShipmentSummary> GetSummaryAsync(Guid profileId, Guid jobId, CancellationToken token = default)
    {
        var (job, scope) = await ResolveAsync(profileId, jobId, token);
        var progress = await checks.GetProgressAsync(scope, token);
        var shipped = await checks.CountByStatusAsync(scope, ShipmentCheckState.Shipped, token);
        var unknown = await checks.CountByStatusAsync(scope, ShipmentCheckState.Unknown, token);
        return new(scope.Inn, scope.Mode, progress?.ReviewedRows ?? 0, job.CodesFound,
            unknown.Values.Sum(), progress?.Completed ?? false, shipped);
    }

    public async Task CheckAvailableAsync(Guid profileId, Guid jobId, ShipmentCheckSession session, Func<Task>? progress = null,
        CancellationToken token = default)
    {
        var (job, scope) = await ResolveAsync(profileId, jobId, token);
        if (session.ProfileId != profileId || session.Inn != scope.Inn)
            throw new InvalidOperationException("Профиль проверки изменился.");
        var saved = await checks.GetProgressAsync(scope, token) ?? new(0, 0);
        if (saved.Completed) { if (progress is not null) await progress(); return; }
        var cursor = new FilePageCursor(saved.NextBatchNumber, saved.ByteOffset);
        while (!session.AuthorizationFailed)
        {
            token.ThrowIfCancellationRequested();
            var page = await files.ReadEntriesPageAsync(jobId, job.NextBatchNumber, null, cursor, token);
            var codes = page.Items.Select(x => x.Code).Distinct(StringComparer.Ordinal).ToArray();
            var known = await checks.GetResultsAsync(profileId, jobId, scope.Mode, scope.Inn, codes, token);
            var results = new System.Collections.Concurrent.ConcurrentDictionary<string, ShipmentCheckResult>(known, StringComparer.Ordinal);
            await Parallel.ForEachAsync(codes.Where(code => !known.ContainsKey(code)),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (code, ct) =>
                {
                    if (session.AuthorizationFailed) return;
                    var result = await session.CheckAsync(code, null, ct);
                    if (session.AuthorizationFailed) return;
                    // Completed outcomes survive cancellation of the rest of this page.
                    await checks.SaveAsync(scope, result, CancellationToken.None);
                    results[code] = result;
                });
            if (session.AuthorizationFailed) break;
            token.ThrowIfCancellationRequested();
            var rows = page.Items.Select(entry => new BulkShipmentCheckedRow(results[entry.Code], entry.Status)).ToArray();
            var next = page.NextCursor ?? new FilePageCursor(job.NextBatchNumber, 0);
            saved = new(next.BatchNumber, next.ByteOffset, page.NextCursor is null && job.State == "Completed", saved.ReviewedRows + rows.Length);
            await checks.SavePageAsync(scope, rows, saved, token);
            if (progress is not null) await progress();
            if (page.NextCursor is null || page.Items.Count == 0) return;
            cursor = next;
        }
    }

    public async Task<FileRecordPage> PreviewPageAsync(Guid profileId, Guid jobId, IReadOnlySet<string> statuses,
        FilePageCursor? cursor = null, CancellationToken token = default)
    {
        var (job, scope) = await ResolveAsync(profileId, jobId, token);
        var checkpoint = await checks.GetProgressAsync(scope, token);
        if (checkpoint is null) return new([], null);
        var end = new FilePageCursor(checkpoint.NextBatchNumber, checkpoint.ByteOffset);
        var result = new List<string>(100);
        do
        {
            var page = await files.ReadEntriesPageAsync(jobId, job.NextBatchNumber, statuses, cursor, token, 100 - result.Count, end);
            var known = await checks.GetResultsAsync(profileId, jobId, scope.Mode, scope.Inn, page.Items.Select(x => x.Code).ToArray(), token);
            result.AddRange(page.Items.Where(entry => known.GetValueOrDefault(entry.Code)?.State == ShipmentCheckState.Shipped).Select(x => x.Code));
            cursor = page.NextCursor;
            if (page.Items.Count == 0) cursor = null;
        } while (result.Count < 100 && cursor is not null);
        return new(result, cursor);
    }

    public async IAsyncEnumerable<string> ExportAsync(Guid profileId, Guid jobId, IReadOnlySet<string> statuses,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default,
        string? expectedInn = null, CrptIntegrationMode? expectedMode = null)
    {
        var (job, scope) = await ResolveAsync(profileId, jobId, token);
        if ((expectedInn is not null && scope.Inn != expectedInn) || (expectedMode is not null && scope.Mode != expectedMode))
            throw new InvalidOperationException("Профиль выгрузки изменился.");
        var checkpoint = await checks.GetProgressAsync(scope, token);
        if (checkpoint is null) yield break;
        var end = new FilePageCursor(checkpoint.NextBatchNumber, checkpoint.ByteOffset);
        FilePageCursor? cursor = null;
        do
        {
            var page = await files.ReadEntriesPageAsync(jobId, job.NextBatchNumber, statuses, cursor, token, endCursor: end);
            var known = await checks.GetResultsAsync(profileId, jobId, scope.Mode, scope.Inn, page.Items.Select(x => x.Code).ToArray(), token);
            foreach (var entry in page.Items)
                if (known.GetValueOrDefault(entry.Code)?.State == ShipmentCheckState.Shipped) yield return entry.Code;
            cursor = page.NextCursor;
            if (page.Items.Count == 0) yield break;
        } while (cursor is not null);
    }

    private async Task<(BulkCodeJob Job, BulkShipmentCheckScope Scope)> ResolveAsync(Guid profileId, Guid jobId, CancellationToken token)
    {
        var job = await jobs.GetAsync(profileId, jobId, token);
        if (job is null || job.Mode != options.Mode) throw new InvalidOperationException("Задание недоступно в текущем профиле и режиме.");
        var profile = await profiles.GetAsync(profileId, token) ?? throw new InvalidOperationException("Профиль не найден.");
        return (job, new(profileId, jobId, options.Mode, profile.Inn ?? ""));
    }
}

