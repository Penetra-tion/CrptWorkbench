using System.Text;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class AggregationTreeJobProcessor(IAggregationTreeRepository jobs, IAggregationTreeSourceStore files,
    IProfileRepository profiles, ITrueApiClient client, IApiRequestLogRepository history, CrptIntegrationOptions options)
{
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        files.CleanupResponses();
        await jobs.RecoverAsync(options.Mode, cancellationToken);
    }

    public async Task ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var job = await jobs.NextAsync(options.Mode, cancellationToken);
        if (job is null) return;
        var profile = await profiles.GetAsync(job.ProfileId, cancellationToken);
        if (profile is null) { await Finish(job, "Failed", "Профиль не найден.", cancellationToken); return; }
        await jobs.TransitionAsync(job.ProfileId, job.Id, options.Mode, ["Queued"], "Running", null, cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                job = (await jobs.GetAsync(job.ProfileId, job.Id, options.Mode, cancellationToken))!;
                if (job is null || job.State is not ("Running" or "Queued" or "PauseRequested" or "CancelRequested")) return;
                if (job.State is "PauseRequested" or "CancelRequested")
                {
                    await jobs.ValidateAsync(job.Id, cancellationToken);
                    await Finish(job, job.State == "PauseRequested" ? "Paused" : "Canceled", job.Error, cancellationToken);
                    return;
                }
                if (job.SourceOffset < job.SourceBytes)
                {
                    await ImportPortion(job, cancellationToken);
                    continue;
                }
                var batches = new List<AggregationFetchBatch>();
                var concurrency = 4;
                for (var i = 0; i < concurrency; i++)
                {
                    var batch = await jobs.TakeBatchAsync(job.Id, cancellationToken);
                    if (batch is null) break;
                    batches.Add(batch);
                }
                if (batches.Count == 0)
                {
                    var current = await jobs.GetAsync(job.ProfileId, job.Id, options.Mode, cancellationToken);
                    if (current?.State is "PauseRequested" or "CancelRequested") continue;
                    await jobs.ValidateAsync(job.Id, cancellationToken);
                    current = await jobs.GetAsync(job.ProfileId, job.Id, options.Mode, cancellationToken);
                    await Finish(job, current is { NodeErrors: 0, RejectedRows: 0 } ? "Completed" : "Partial", null, cancellationToken);
                    return;
                }
                var denied = 0;
                await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
                    async (batch, token) =>
                    {
                        if (Volatile.Read(ref denied) != 0 || !await CanDispatch(job, token))
                        { await jobs.ResetBatchAsync(job.Id, batch, token); return; }
                        var status = await Fetch(job, profile, batch, () => Volatile.Read(ref denied) != 0, token);
                        if (status is 401 or 403) Interlocked.Exchange(ref denied, 1);
                    });
                if (denied != 0)
                {
                    await jobs.ValidateAsync(job.Id, cancellationToken);
                    await Finish(job, "Partial", "Дальнейшая загрузка остановлена: сервис отклонил доступ (HTTP 401/403).", cancellationToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* Recovery requires manual continuation. */ }
        catch (Exception exception)
        {
            var error = exception is IOException
                ? "Ошибка чтения или записи данных. Проверьте свободное место и продолжите задание."
                : "Не удалось обработать задание. Сохранённые данные доступны; можно продолжить вручную.";
            await jobs.TransitionAsync(job.ProfileId, job.Id, options.Mode, ["Running", "Queued", "PauseRequested"], "Paused", error, CancellationToken.None);
        }
    }

    private async Task<bool> CanDispatch(AggregationTreeJob job, CancellationToken ct) =>
        (await jobs.GetAsync(job.ProfileId, job.Id, options.Mode, ct))?.State is "Running" or "Queued";

    private async Task<int> Fetch(AggregationTreeJob job, CrptProfile profile, AggregationFetchBatch batch, Func<bool> denied, CancellationToken ct)
    {
        try
        {
            var request = new IntegrationRequest(TrueApiAggregationTreeHandler.OperationId, "POST",
                client.BaseAddress.AbsoluteUri.TrimEnd('/') + "/cises/info",
                new Dictionary<string, string> { ["Accept"] = "application/json", ["Content-Type"] = "application/json", ["X-Correlation-Id"] = job.Id.ToString("N") },
                JsonSerializer.Serialize(batch.Codes), job.Id.ToString("N"), new(profile.Id, profile.Inn, profile.CertificateThumbprint))
            {
                CanDispatchAsync = async token => !denied() && await CanDispatch(job, token),
                BeforeDispatchAsync = async token =>
                {
                    if (denied() || !await CanDispatch(job, token)) throw new OperationCanceledException(token);
                }
            };
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (denied() || !await CanDispatch(job, ct)) { await jobs.ResetBatchAsync(job.Id, batch, ct); return 0; }
                int status;
                await using (var response = files.CreateResponse(job.Id, batch.Id))
                {
                    IntegrationExchange exchange;
                    try { exchange = await client.ExecuteToStreamAsync(request, response, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (ApiDispatchNotStartedException.IsPresent(exception))
                    {
                        if (!denied()) await jobs.TransitionAsync(job.ProfileId, job.Id, options.Mode,
                            ["Queued", "Running"], "PauseRequested",
                            "Запрос не был отправлен. Проверьте подключение и продолжите задание вручную.", ct);
                        await jobs.ResetBatchAsync(job.Id, batch, ct);
                        return 0;
                    }
                    catch (IOException) { throw; }
                    catch (Exception)
                    {
                        await jobs.PublishAsync(job.Id, batch, "Не удалось получить сведения по партии кодов.", ct);
                        return 502;
                    }
                    status = exchange.StatusCode;
                    if (status == 429 && attempt < 2)
                    {
                        if (!await ApiReadRetry.WaitAsync(exchange.ResponseHeaders, attempt, ct, async token => !denied() && await CanDispatch(job, token)))
                        {
                            await jobs.ResetBatchAsync(job.Id, batch, ct);
                            return 0;
                        }
                        continue;
                    }
                    if (status is >= 200 and < 300 or 404)
                    {
                        response.Position = 0;
                        try
                        {
                            var sink = new Sink(jobs, job.Id, batch.Id);
                            await AggregationTreeResponseReader.ReadAsync(response, batch.Codes.ToHashSet(StringComparer.Ordinal), sink, ct);
                            await sink.FlushAsync(ct);
                            await jobs.PublishAsync(job.Id, batch, null, ct);
                        }
                        catch (InvalidDataException) { await jobs.PublishAsync(job.Id, batch, "Некорректный или неоднозначный ответ по партии кодов.", ct); }
                    }
                    else await jobs.PublishAsync(job.Id, batch, $"Сервис вернул HTTP {status}.", ct);
                }
                return status;
            }
            return 429;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await jobs.ResetBatchAsync(job.Id, batch, CancellationToken.None); throw; }
        catch (IOException)
        { await jobs.ResetBatchAsync(job.Id, batch, CancellationToken.None); throw; }
        catch (Exception) { await jobs.ResetBatchAsync(job.Id, batch, CancellationToken.None); throw; }
        finally { files.DeleteResponse(job.Id, batch.Id); }
    }

    private async Task ImportPortion(AggregationTreeJob job, CancellationToken ct)
    {
        await using var source = files.OpenRead(job.Id, job.SourceOffset);
        var reader = new SourceLineReader(source);
        var rows = new List<AggregationSeed>(1000);
        for (var index = 0; index < 1000; index++)
        {
            var line = await reader.ReadAsync(ct);
            if (line is null) break;
            var number = job.LinesRead + index + 1;
            var value = line.Value.Text;
            if (number == 1) value = value.TrimStart('\uFEFF');
            if (value.Length == 0 && line.Value.Error is null) { rows.Add(new(number, null)); continue; }
            var code = MarkingCodeInput.TrimAfterGroupSeparator(value);
            var reason = line.Value.Error ?? (code.Length is < 18 or > 74 || code.Any(char.IsControl) || MarkingCodeFormat.HasBracketedApplicationIdentifiers(code)
                ? "Укажите код от 18 до 74 символов без управляющих символов и скобок вокруг идентификаторов 01 и 21." : null);
            rows.Add(reason is null ? new(number, code) : new(number, null, value.Length > 256 ? value[..256] : value, reason));
        }
        await jobs.ImportAsync(job.Id, job.SourceOffset + reader.BytesRead, rows, ct);
    }

    private async Task Finish(AggregationTreeJob job, string state, string? error, CancellationToken ct)
    {
        string[] expected = state switch { "Paused" => ["PauseRequested"], "Canceled" => ["CancelRequested"], _ => ["Running", "Queued"] };
        if (!await jobs.TransitionAsync(job.ProfileId, job.Id, options.Mode, expected, state, error, ct)) return;
        var current = await jobs.GetAsync(job.ProfileId, job.Id, options.Mode, ct);
        if (current is null) return;
        await history.UpsertBulkSummaryAsync(new ApiRequestLog
        {
            Id = job.Id, ProfileId = job.ProfileId, CreatedAt = job.CreatedAt, CreatedAtUnixMs = job.CreatedAt.ToUnixTimeMilliseconds(),
            OperationId = TrueApiAggregationTreeHandler.OperationId, System = ApiSystem.TrueApi, CorrelationId = job.Id.ToString("N"),
            HttpMethod = "POST", Url = "aggregation-tree-job", RequestHeaders = "{}", ResponseHeaders = "{}",
            RequestBody = JsonSerializer.Serialize(new { jobId = job.Id, job.IncludeSiblings }),
            ResponseBody = JsonSerializer.Serialize(new { kind = "aggregation-tree-job", jobId = job.Id, state, current.LinesRead, current.NodesTotal, current.NodesLoaded, current.NodeErrors, current.RejectedRows }),
            StatusCode = state == "Completed" ? 200 : state == "Partial" ? 207 : null, Success = state == "Completed", Error = error,
            DurationMs = (long)(DateTimeOffset.UtcNow - job.CreatedAt).TotalMilliseconds
        }, ct);
    }

    private sealed class Sink(IAggregationTreeRepository repository, Guid jobId, Guid batchId) : IAggregationTreeResponseSink
    {
        private readonly List<AggregationResponseNode> _nodes = new(1000);
        public async ValueTask AddChildrenAsync(string code, IReadOnlyList<string> children, CancellationToken cancellationToken) =>
            await repository.StageChildrenAsync(jobId, batchId, code, children, cancellationToken);
        public ValueTask AddNodeAsync(AggregationResponseNode node, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _nodes.Add(node);
            return ValueTask.CompletedTask;
        }
        public Task FlushAsync(CancellationToken cancellationToken) => repository.StageNodesAsync(jobId, batchId, _nodes, cancellationToken);
    }

    // Bounded invalid-line retention, exact byte offsets and strict UTF-8 allow good rows after a bad row.
    private sealed class SourceLineReader(Stream source)
    {
        private readonly byte[] _buffer = new byte[65536];
        private int _position, _length;
        public long BytesRead { get; private set; }
        public async Task<(string Text, string? Error)?> ReadAsync(CancellationToken ct)
        {
            var bytes = new List<byte>(256);
            var any = false;
            var tooLong = false;
            while (true)
            {
                if (_position == _length)
                {
                    _length = await source.ReadAsync(_buffer, ct); _position = 0;
                    if (_length == 0) break;
                }
                var value = _buffer[_position++]; BytesRead++; any = true;
                if (value == 10) break;
                if (bytes.Count < 4096) bytes.Add(value); else tooLong = true;
            }
            if (!any) return null;
            if (bytes.Count > 0 && bytes[^1] == 13) bytes.RemoveAt(bytes.Count - 1);
            if (tooLong) return (Encoding.UTF8.GetString(bytes.ToArray()), "Слишком длинная строка.");
            try { return (new UTF8Encoding(false, true).GetString(bytes.ToArray()), null); }
            catch (DecoderFallbackException) { return (Encoding.UTF8.GetString(bytes.ToArray()), "Некорректная кодировка UTF-8."); }
        }
    }
}
