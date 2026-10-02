using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class BulkCodeFileService(
    IBulkCodeJobRepository jobs, IBulkCodeFileStore files, IProfileRepository profiles,
    CrptIntegrationOptions options)
{
    public async Task<BulkCodeJob> UploadAsync(Guid profileId, string fileName, long length, Stream source, CancellationToken cancellationToken)
    {
        if (length <= 0) throw new ArgumentException("Выберите непустой TXT-файл.");
        if (await profiles.GetAsync(profileId, cancellationToken) is null) throw new InvalidOperationException("Профиль не найден.");
        var job = new BulkCodeJob
        {
            ProfileId = profileId,
            Mode = options.Mode,
            FileName = Path.GetFileName(fileName).Length > 260 ? Path.GetFileName(fileName)[..260] : Path.GetFileName(fileName),
            FileBytes = length
        };
        try
        {
            await files.SaveSourceAsync(job.Id, source, cancellationToken);
            job.FileBytes = files.SourceLength(job.Id);
            await jobs.AddAsync(job, cancellationToken);
            return job;
        }
        catch
        {
            files.Delete(job.Id);
            throw;
        }
    }

    public async Task<BulkCodeJob> PrepareAsync(Guid profileId, long length, Stream source, CancellationToken cancellationToken)
    {
        if (length <= 0) throw new ArgumentException("Запрос не вернул кодов.");
        if (await profiles.GetAsync(profileId, cancellationToken) is null) throw new InvalidOperationException("Профиль не найден.");
        var job = new BulkCodeJob
        {
            ProfileId = profileId, Mode = options.Mode, FileName = "Из базы TraceX",
            FileBytes = length, State = "Ready"
        };
        try
        {
            await files.SaveSourceAsync(job.Id, source, cancellationToken);
            job.FileBytes = files.SourceLength(job.Id);
            await jobs.AddAsync(job, cancellationToken);
            return job;
        }
        catch { files.Delete(job.Id); throw; }
    }

    public Task<bool> StartReadyAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        jobs.StartReadyAsync(profileId, id, cancellationToken);

    public Task<RecordPage<BulkCodeJob>> ListPageAsync(Guid profileId, RecordPageCursor? cursor = null, CancellationToken cancellationToken = default) =>
        jobs.ListPageAsync(profileId, cursor, cancellationToken);

    public Task<IReadOnlyList<BulkCodeJob>> ListAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        jobs.ListAsync(profileId, cancellationToken);

    public Task<IReadOnlyList<BulkCodeJob>> GetManyAsync(Guid profileId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        jobs.GetManyAsync(profileId, ids, cancellationToken);

    public Task<BulkCodeJob?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        jobs.GetAsync(profileId, id, cancellationToken);

    public async Task CancelAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(profileId, id, cancellationToken);
        if (job?.State is "Queued" or "Running") await jobs.SetJobStateAsync(profileId, id, "CancelRequested", null, cancellationToken);
    }

    public Task RetryAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        jobs.RetryFailuresAsync(profileId, id, cancellationToken);

    public async Task ResumeAfterDiskErrorAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(profileId, id, cancellationToken);
        if (job?.State == "Failed" && job.Error?.StartsWith("Недостаточно места", StringComparison.Ordinal) == true)
            await jobs.SetJobStateAsync(profileId, id, "Queued", null, cancellationToken);
    }

    public async Task DeleteAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(profileId, id, cancellationToken);
        if (job is null) return;
        if (job.State is "Queued" or "Running" or "CancelRequested")
            throw new InvalidOperationException("Сначала отмените и дождитесь остановки задания.");
        files.Delete(id);
        await jobs.DeleteAsync(profileId, id, cancellationToken);
    }

    public async IAsyncEnumerable<string> ExportAsync(Guid profileId, Guid id, IReadOnlySet<string> statuses,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(profileId, id, cancellationToken);
        if (job is null)
            throw new InvalidOperationException("Задание не найдено.");
        await foreach (var code in files.ReadCodesAsync(id, job.NextBatchNumber, statuses, cancellationToken)) yield return code;
    }

    public async Task<IReadOnlyDictionary<string, long>> CountCodesByStatusAsync(Guid profileId, Guid id,
        CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(profileId, id, cancellationToken)
            ?? throw new InvalidOperationException("Задание не найдено.");
        return await files.CountCodesByStatusAsync(id, job.NextBatchNumber, cancellationToken);
    }

    public async Task<FileRecordPage> PreviewPageAsync(Guid profileId, Guid id, IReadOnlySet<string> statuses,
        FilePageCursor? cursor = null, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(profileId, id, cancellationToken)
            ?? throw new InvalidOperationException("Задание не найдено.");
        if (job.State != "Completed") throw new InvalidOperationException("Предпросмотр доступен после завершения задания.");
        return await files.ReadPageAsync(id, job.NextBatchNumber, statuses, cursor, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> PreviewAsync(Guid profileId, Guid id, IReadOnlySet<string> statuses,
        CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(profileId, id, cancellationToken)
            ?? throw new InvalidOperationException("Задание не найдено.");
        if (job.State != "Completed")
            throw new InvalidOperationException("Предпросмотр доступен после завершения задания.");
        var codes = new List<string>();
        await foreach (var code in files.ReadCodesAsync(id, job.NextBatchNumber, statuses, cancellationToken))
        {
            codes.Add(code);
        }
        return codes;
    }
}
