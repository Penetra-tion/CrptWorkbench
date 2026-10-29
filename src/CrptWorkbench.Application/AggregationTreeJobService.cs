using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class AggregationTreeJobService(IAggregationTreeRepository jobs, IAggregationTreeSourceStore files,
    IProfileRepository profiles, CrptIntegrationOptions options)
{
    public async Task<AggregationTreeJob> PrepareAsync(Guid profileId, string name, Stream source, CancellationToken cancellationToken = default)
    {
        if (await profiles.GetAsync(profileId, cancellationToken) is null) throw new InvalidOperationException("Профиль не найден.");
        var id = Guid.NewGuid();
        try
        {
            var bytes = await files.SaveAsync(id, source, cancellationToken);
            if (bytes == 0) throw new ArgumentException("Список кодов пуст.");
            var safeName = Path.GetFileName(name);
            await jobs.CreateAsync(id, profileId, options.Mode, safeName.Length > 260 ? safeName[..260] : safeName, bytes, cancellationToken);
            return (await GetAsync(profileId, id, cancellationToken))!;
        }
        catch { files.Delete(id); throw; }
    }
    public Task<bool> StartAsync(Guid profileId, Guid id, bool includeSiblings, CancellationToken cancellationToken = default) =>
        jobs.StartAsync(profileId, id, options.Mode, includeSiblings, cancellationToken);
    public Task<AggregationTreeJob?> GetAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        jobs.GetAsync(profileId, id, options.Mode, cancellationToken);
    public Task<IReadOnlyList<AggregationTreeJob>> GetManyAsync(Guid profileId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        jobs.GetManyAsync(profileId, ids, options.Mode, cancellationToken);
    public Task<IReadOnlyList<AggregationTreeJob>> ListAsync(Guid profileId, long? before = null, CancellationToken cancellationToken = default) =>
        jobs.ListAsync(profileId, options.Mode, before, cancellationToken);
    public async Task PauseAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        await jobs.TransitionAsync(profileId, id, options.Mode, ["Queued", "Running"], "PauseRequested", null, cancellationToken);
    public async Task ResumeAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default) =>
        await jobs.TransitionAsync(profileId, id, options.Mode, ["Paused"], "Queued", null, cancellationToken);
    public async Task CancelAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default)
    {
        if (!await jobs.TransitionAsync(profileId, id, options.Mode, ["Ready", "Paused"], "Canceled", null, cancellationToken))
            await jobs.TransitionAsync(profileId, id, options.Mode, ["Queued", "Running", "PauseRequested"], "CancelRequested", null, cancellationToken);
    }
    public async Task DeleteAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default)
    {
        if (await GetAsync(profileId, id, cancellationToken) is null) return;
        if (!await jobs.TransitionAsync(profileId, id, options.Mode, ["Ready", "Paused", "Canceled", "Completed", "Partial", "Failed", "Deleting"], "Deleting", null, cancellationToken))
            throw new InvalidOperationException("Сначала остановите задание.");
        files.Delete(id);
        await jobs.DeleteAsync(id, cancellationToken);
    }
    private async Task Require(Guid profileId, Guid id, CancellationToken ct)
    {
        if (await GetAsync(profileId, id, ct) is null) throw new InvalidOperationException("Задание не найдено.");
    }
    public async Task<AggregationNodePage> NodesAsync(Guid profileId, Guid id, string? parent = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        await Require(profileId, id, cancellationToken);
        return await jobs.NodesAsync(id, parent, cursor, cancellationToken);
    }
    public async Task<IReadOnlyList<string>> TypesAsync(Guid profileId, Guid id, CancellationToken cancellationToken = default)
    {
        await Require(profileId, id, cancellationToken);
        return await jobs.TypesAsync(id, cancellationToken);
    }
    public async Task<AggregationRejectedPage> RejectedAsync(Guid profileId, Guid id, long? after = null, CancellationToken cancellationToken = default)
    {
        await Require(profileId, id, cancellationToken);
        return await jobs.RejectedAsync(id, after, cancellationToken);
    }
    public Task<AggregationExportFile> ExportAsync(Guid profileId, Guid id, AggregationExportOptions exportOptions, CancellationToken cancellationToken = default) =>
        jobs.ExportAsync(profileId, id, options.Mode, exportOptions, cancellationToken);
    public async Task<Stream> OpenExportAsync(Guid profileId, Guid id, Guid exportId, CancellationToken cancellationToken = default)
    {
        await Require(profileId, id, cancellationToken);
        return files.OpenExport(id, exportId);
    }
}
