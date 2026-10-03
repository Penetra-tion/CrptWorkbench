using CrptWorkbench.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CrptWorkbench.Infrastructure;

public sealed class SuzUtilisationRepository(IDbContextFactory<WorkbenchDbContext> factory, IDataProtectionProvider protection) : ISuzUtilisationRepository
{
    private readonly IDataProtector protector = protection.CreateProtector("CRPT.SuzUtilisation.Payload.v1");
    public async Task AddAsync(SuzUtilisationJob job, IReadOnlyList<SuzUtilisationDraft> drafts, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.SuzUtilisationJobs.Add(job);
        foreach (var draft in drafts) { draft.Report.ProtectedPayload = protector.Protect(draft.Json); db.SuzUtilisationReports.Add(draft.Report); }
        await db.SaveChangesAsync(cancellationToken);
    }
    public async Task<SuzUtilisationJobDetails?> GetAsync(Guid profileId, CrptIntegrationMode mode, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var job = await db.SuzUtilisationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId && x.ProfileId == profileId && x.Mode == mode, cancellationToken);
        if (job is null) return null;
        var reports = await db.SuzUtilisationReports.AsNoTracking().Where(x => x.JobId == jobId).OrderBy(x => x.Number).ToListAsync(cancellationToken);
        foreach (var report in reports) report.ProtectedPayload = "";
        return new(job, reports);
    }
    public async Task<RecordPage<SuzUtilisationJob>> ListPageAsync(Guid profileId, CrptIntegrationMode mode, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.SuzUtilisationJobs.AsNoTracking().Where(x => x.ProfileId == profileId && x.Mode == mode);
        var total = await query.LongCountAsync(cancellationToken);
        if (cursor is not null) { var ids = cursor.BoundaryIds.ToArray(); query = query.Where(x => x.CreatedAtUnixMs < cursor.CreatedAtUnixMs || x.CreatedAtUnixMs == cursor.CreatedAtUnixMs && !ids.Contains(x.Id)); }
        var rows = await query.OrderByDescending(x => x.CreatedAtUnixMs).ThenBy(x => x.Id).Take(101).ToListAsync(cancellationToken);
        var items = rows.Take(100).ToArray();
        RecordPageCursor? next = null;
        if (rows.Count > 100) { var stamp = items[^1].CreatedAtUnixMs; IEnumerable<Guid> ids = items.Where(x => x.CreatedAtUnixMs == stamp).Select(x => x.Id); if (cursor?.CreatedAtUnixMs == stamp) ids = cursor.BoundaryIds.Concat(ids); next = new(stamp, ids.ToArray()); }
        return new(items, next, total);
    }
    public async Task<IReadOnlyList<SuzUtilisationJob>> GetManyAsync(Guid profileId, CrptIntegrationMode mode, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var result = new List<SuzUtilisationJob>();
        foreach (var part in ids.Chunk(500)) result.AddRange(await db.SuzUtilisationJobs.AsNoTracking().Where(x => x.ProfileId == profileId && x.Mode == mode && part.Contains(x.Id)).ToListAsync(cancellationToken));
        return result.OrderByDescending(x => x.CreatedAtUnixMs).ThenBy(x => x.Id).ToArray();
    }
    public async Task<bool> ClaimSubmissionAsync(Guid jobId, Guid owner, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.SuzUtilisationJobs.Where(x => x.Id == jobId && x.SubmissionOwner == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.SubmissionOwner, owner), cancellationToken) == 1;
    }
    public async Task ReleaseSubmissionAsync(Guid jobId, Guid owner, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.SuzUtilisationJobs.Where(x => x.Id == jobId && x.SubmissionOwner == owner).ExecuteUpdateAsync(s => s.SetProperty(x => x.SubmissionOwner, (Guid?)null), cancellationToken);
    }
    public async Task<string?> StartReportAsync(Guid jobId, Guid reportId, Guid owner, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.SuzUtilisationJobs.AnyAsync(x => x.Id == jobId && x.SubmissionOwner == owner, cancellationToken)) return null;
        var payload = await db.SuzUtilisationReports.Where(x => x.Id == reportId && x.JobId == jobId && x.State == "Prepared").Select(x => x.ProtectedPayload).SingleOrDefaultAsync(cancellationToken);
        if (payload is null) return null;
        string json;
        try { json = protector.Unprotect(payload); }
        catch (System.Security.Cryptography.CryptographicException) { throw new ArgumentException("Не удалось открыть защищённые данные отчёта. Проверьте доступность ключей защиты данных."); }
        var changed = await db.SuzUtilisationReports.Where(x => x.Id == reportId && x.JobId == jobId && x.State == "Prepared").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Sending"), cancellationToken);
        return changed == 1 ? json : null;
    }
    public async Task CompleteSubmissionAsync(SuzUtilisationReport report, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.SuzUtilisationReports.Where(x => x.Id == report.Id && x.State == "Sending").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, report.State).SetProperty(x => x.ReportId, report.ReportId).SetProperty(x => x.Error, report.Error).SetProperty(x => x.SubmittedAtUnixMs, report.SubmittedAtUnixMs).SetProperty(x => x.NextCheckAtUnixMs, report.NextCheckAtUnixMs).SetProperty(x => x.TrackingUntilUnixMs, report.TrackingUntilUnixMs), cancellationToken);
    }
    public async Task RecoverSendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.SuzUtilisationReports.Where(x => x.State == "Sending").ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Unknown").SetProperty(x => x.Error, "Отправка была прервана. Исход неизвестен; повторная отправка заблокирована."), cancellationToken);
        await db.SuzUtilisationJobs.Where(x => x.SubmissionOwner != null).ExecuteUpdateAsync(s => s.SetProperty(x => x.SubmissionOwner, (Guid?)null), cancellationToken);
    }
    public async Task<IReadOnlyList<SuzUtilisationReport>> ClaimDueAsync(long now, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ids = await db.SuzUtilisationReports.Where(x => x.State == "Tracking" && (x.NextCheckAtUnixMs <= now || x.TrackingUntilUnixMs <= now) && (x.LeaseUntilUnixMs == null || x.LeaseUntilUnixMs <= now)).OrderBy(x => x.NextCheckAtUnixMs).Take(take).Select(x => x.Id).ToListAsync(cancellationToken);
        var result = new List<SuzUtilisationReport>();
        foreach (var id in ids)
        {
            var owner = Guid.NewGuid();
            if (await db.SuzUtilisationReports.Where(x => x.Id == id && x.State == "Tracking" && (x.LeaseUntilUnixMs == null || x.LeaseUntilUnixMs <= now)).ExecuteUpdateAsync(s => s.SetProperty(x => x.TrackingOwner, owner).SetProperty(x => x.LeaseUntilUnixMs, now + 120000), cancellationToken) == 1)
            { var report = await db.SuzUtilisationReports.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken); report.ProtectedPayload = ""; result.Add(report); }
        }
        return result;
    }
    public async Task<SuzUtilisationJob?> GetJobForTrackingAsync(Guid jobId, CancellationToken cancellationToken = default)
    { await using var db = await factory.CreateDbContextAsync(cancellationToken); return await db.SuzUtilisationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken); }
    public async Task<bool> CompleteCheckAsync(SuzUtilisationReport report, Guid owner, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.SuzUtilisationReports.Where(x => x.Id == report.Id && x.State == "Tracking" && x.TrackingOwner == owner).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, report.State).SetProperty(x => x.RemoteStatus, report.RemoteStatus).SetProperty(x => x.ReceiptState, report.ReceiptState).SetProperty(x => x.Error, report.Error).SetProperty(x => x.PollCount, report.PollCount).SetProperty(x => x.LastCheckedAtUnixMs, report.LastCheckedAtUnixMs).SetProperty(x => x.NextCheckAtUnixMs, report.NextCheckAtUnixMs).SetProperty(x => x.TrackingOwner, (Guid?)null).SetProperty(x => x.LeaseUntilUnixMs, (long?)null), cancellationToken) == 1;
    }
    public async Task<bool> ScheduleAsync(Guid profileId, CrptIntegrationMode mode, Guid reportId, long now, bool resume, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.SuzUtilisationReports.Where(x => x.Id == reportId && x.ReportId != null && x.State == (resume ? "Paused" : "Tracking") && db.SuzUtilisationJobs.Any(j => j.Id == x.JobId && j.ProfileId == profileId && j.Mode == mode));
        return await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Tracking").SetProperty(x => x.NextCheckAtUnixMs, now).SetProperty(x => x.TrackingUntilUnixMs, x => resume ? now + 86400000 : x.TrackingUntilUnixMs).SetProperty(x => x.TrackingOwner, (Guid?)null).SetProperty(x => x.LeaseUntilUnixMs, (long?)null), cancellationToken) == 1;
    }
}


