using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class BulkCodeJobProcessor(
    IBulkCodeJobRepository jobs, IBulkCodeFileStore files, IProfileRepository profiles,
    ITrueApiClient client, IApiRequestLogRepository history, CrptIntegrationOptions options)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex CodeCharacters = new("^[A-Za-z0-9%&'\"()*+,_./:;<=>?!-]{18,74}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim _batchPersistenceGate = new(1, 1);

    public async Task ProcessNextAsync(CancellationToken cancellationToken)
    {
        var job = await jobs.NextRunnableAsync(options.Mode, cancellationToken);
        if (job is null) return;
        var jobId = job.Id;
        var profileId = job.ProfileId;
        if (job.Mode != options.Mode) return;
        var profile = await profiles.GetAsync(job.ProfileId, cancellationToken);
        if (profile is null)
        {
            await jobs.SetJobStateAsync(job.ProfileId, job.Id, "Failed", "Профиль не найден.", cancellationToken);
            return;
        }

        try
        {
            var active = new Dictionary<long, Task>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    job = await jobs.GetAsync(profileId, jobId, cancellationToken);
                    if (job is null || job.State is "Canceled" or "Completed" or "Partial" or "Failed" or "Paused" or "Deleting") return;
                    if (job.State is "CancelRequested" or "PauseRequested")
                    {
                        if (active.Count > 0)
                        {
                            await Task.WhenAll(active.Values);
                            active.Clear();
                            continue; // Reload: cancellation can supersede a pause while requests drain.
                        }
                        await FinishAsync(job, job.State == "CancelRequested" ? "Canceled" : "Paused", job.Error, cancellationToken);
                        return;
                    }

                    while (active.Count < 4)
                    {
                        job = await jobs.GetAsync(profileId, jobId, cancellationToken);
                        if (job is null || job.State is not ("Queued" or "Running")) break;
                        var pending = await jobs.ListBatchesAsync(jobId, "Running", 4, cancellationToken);
                        var queued = await jobs.ListBatchesAsync(jobId, "Queued", 4, cancellationToken);
                        var next = pending.Concat(queued).FirstOrDefault(batch => !active.ContainsKey(batch.Number));
                        if (next is null && job.NextOffset < job.FileBytes) next = await PrepareBatchAsync(job, cancellationToken);
                        if (next is null) break;
                        active.Add(next.Number, ProcessBatchAsync(job, profile, next, cancellationToken));
                    }
                    if (active.Count == 0)
                    {
                        job = await jobs.GetAsync(profileId, jobId, cancellationToken);
                        if (job is null) return;
                        if (job.State is "PauseRequested" or "CancelRequested") continue;
                        await FinishAsync(job, job.FailedBatches > 0 ? "Partial" : "Completed", null, cancellationToken);
                        return;
                    }
                    var completed = await Task.WhenAny(active.Values);
                    await completed;
                    active.Remove(active.First(pair => ReferenceEquals(pair.Value, completed)).Key);
                }
            }
            finally { await Task.WhenAll(active.Values); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var message = exception is IOException ? "Недостаточно места на диске или ошибка чтения файла." :
                exception is InvalidDataException or DecoderFallbackException ? exception.Message : "Ошибка обработки файла.";
            var current = await jobs.GetAsync(profileId, jobId, CancellationToken.None);
            if (current is not null && current.State is not ("Canceled" or "Completed" or "Partial" or "Failed" or "Paused" or "Deleting"))
                await FinishAsync(current, current.State == "CancelRequested" ? "Canceled" : "Failed", message, CancellationToken.None);
        }
    }

    private async Task FinishAsync(BulkCodeJob job, string state, string? error, CancellationToken cancellationToken)
    {
        var expected = state switch
        {
            "Paused" => new[] { "PauseRequested" },
            "Canceled" => new[] { "CancelRequested" },
            "Failed" => new[] { "Queued", "Running", "PauseRequested" },
            _ => new[] { "Queued", "Running" }
        };
        if (!await jobs.TransitionStateAsync(job.ProfileId, job.Id, job.Mode, expected, state, error, cancellationToken)) return;
        var current = await jobs.GetAsync(job.ProfileId, job.Id, cancellationToken);
        if (current is null) return;
        var summary = JsonSerializer.Serialize(new
        {
            jobId = job.Id, state, current.FileBytes, current.LinesRead, current.CodesCompleted,
            current.CodesFound, current.CodeErrors, current.FailedBatches
        }, JsonOptions);
        await history.UpsertBulkSummaryAsync(new ApiRequestLog
        {
            Id = job.Id,
            ProfileId = job.ProfileId,
            CreatedAt = job.CreatedAt,
            CreatedAtUnixMs = job.CreatedAtUnixMs,
            Environment = CrptEnvironment.Production,
            System = ApiSystem.TrueApi,
            OperationId = "true-api.code-info-bulk-file",
            CorrelationId = job.Id.ToString("N"),
            HttpMethod = "POST",
            Url = "bulk-file-job",
            RequestHeaders = "{}",
            ResponseHeaders = "{}",
            RequestBody = JsonSerializer.Serialize(new { jobId = job.Id, current.FileBytes }, JsonOptions),
            ResponseBody = summary,
            StatusCode = state == "Completed" ? 200 : state == "Partial" ? 207 : null,
            Success = state == "Completed",
            Error = error ?? (state == "Partial" ? "Часть партий завершилась ошибкой." : null),
            DurationMs = (long)(DateTimeOffset.UtcNow - job.CreatedAt).TotalMilliseconds
        }, cancellationToken);
    }

    private async Task<BulkCodeBatch> PrepareBatchAsync(BulkCodeJob job, CancellationToken cancellationToken)
    {
        await using var raw = files.OpenSource(job.Id, job.NextOffset);
        var reader = new BoundedLineReader(raw);
        var count = 0;
        var lines = 0L;
        var rejected = new List<string>();
        while (lines < 1000 && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines++;
            if (line.Length == 0) continue;
            if (job.NextOffset == 0 && lines == 1 && line[0] == '\uFEFF') line = line[1..];
            var reason = reader.Error ?? ValidationError(MarkingCodeInput.TrimAfterGroupSeparator(line));
            if (reason is not null)
            {
                rejected.Add($"Строка {job.LinesRead + lines}: {reason} Значение: {JsonSerializer.Serialize(line)}");
                continue;
            }
            count++;
        }
        var batch = new BulkCodeBatch
        {
            JobId = job.Id,
            Number = job.NextBatchNumber,
            StartOffset = job.NextOffset,
            EndOffset = reader.Position,
            CodeCount = count
        };
        if (batch.EndOffset <= batch.StartOffset) throw new InvalidDataException("Не удалось прочитать исходный файл.");
        await files.WriteRejectedAsync(job.Id, batch.Number, rejected, cancellationToken);
        await jobs.AddBatchAsync(batch, job.LinesRead + lines, cancellationToken);
        return batch;
    }

    private async Task ProcessBatchAsync(BulkCodeJob job, CrptProfile profile, BulkCodeBatch batch, CancellationToken cancellationToken)
    {
        var fileOperation = true;
        try
        {
            if (files.FragmentExists(job.Id, batch.Number))
            {
                var (existingFound, existingErrors) = files.FragmentSummary(job.Id, batch.Number);
                await CompleteBatchAsync(job.Id, batch.Number, existingFound, existingErrors, cancellationToken);
                return;
            }
            if (batch.CodeCount == 0)
            {
                await files.WriteFragmentAsync(job.Id, batch.Number, [], 0, 0, cancellationToken);
                await CompleteBatchAsync(job.Id, batch.Number, 0, 0, cancellationToken);
                return;
            }
            var codes = await ReadCodesAsync(job.Id, batch, cancellationToken);
            if (codes.Length != batch.CodeCount) throw new InvalidDataException("Исходный файл изменился после загрузки.");
            fileOperation = false;
            var correlationId = Guid.NewGuid().ToString("N");
            var request = new IntegrationRequest(TrueApiBulkCodeInfoHandler.OperationId, "POST",
                $"{client.BaseAddress.AbsoluteUri.TrimEnd('/')}/cises/info",
                new Dictionary<string, string> { ["Accept"] = "application/json", ["Content-Type"] = "application/json", ["X-Correlation-Id"] = correlationId },
                JsonSerializer.Serialize(codes, JsonOptions), correlationId,
                new(profile.Id, profile.Inn, profile.CertificateThumbprint))
            {
                CanDispatchAsync = async token =>
                    (await jobs.GetAsync(job.ProfileId, job.Id, token))?.State is "Queued" or "Running",
                // Recheck after waiting for a shared service slot, and on the
                // client's automatic 401 retry, immediately before HTTP dispatch.
                BeforeDispatchAsync = async token =>
                {
                    if (!await jobs.TryBeginBatchAsync(job.ProfileId, job.Id, job.Mode, batch.Number, token))
                        throw new OperationCanceledException(token);
                }
            };
            if (!await jobs.TryBeginBatchAsync(job.ProfileId, job.Id, job.Mode, batch.Number, cancellationToken)) return;
            IntegrationExchange? exchange = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0 && !await jobs.TryBeginBatchAsync(job.ProfileId, job.Id, job.Mode, batch.Number, cancellationToken)) return;
                exchange = await client.ExecuteAsync(request, cancellationToken);
                if (exchange.StatusCode is 401 or 403)
                {
                    await jobs.TransitionStateAsync(job.ProfileId, job.Id, job.Mode, ["Queued", "Running"],
                        "PauseRequested", $"Сервис отклонил доступ (HTTP {exchange.StatusCode}). Проверьте авторизацию и продолжите задание.", cancellationToken);
                    return; // Keep this batch resumable, without failed counters.
                }
                if (exchange.StatusCode != 429 || attempt == 2) break;
                if (!await ApiReadRetry.WaitAsync(exchange.ResponseHeaders, attempt, cancellationToken, async token =>
                    (await jobs.GetAsync(job.ProfileId, job.Id, token))?.State is "Queued" or "Running")) return;
            }
            if (exchange is null || exchange.StatusCode is not (>= 200 and < 300) and not 404)
                throw new InvalidDataException($"Ошибка HTTP {exchange?.StatusCode}.");
            var result = CodeInfoResults.Parse(exchange.ResponseBody);
            var requested = codes.ToHashSet(StringComparer.Ordinal);
            var received = result?.Items.Where(x => x.CisInfo?.RequestedCis is not null)
                .Select(x => x.CisInfo!.RequestedCis!).ToHashSet(StringComparer.Ordinal);
            if (result is null || result.Items.Count != codes.Length ||
                received is null || !requested.SetEquals(received) ||
                result.Items.Any(x => x.CisInfo?.RequestedCis is null))
                throw new InvalidDataException("ЧЗ вернул неполный или некорректный ответ.");
            var found = result.FoundCount;
            var entries = result.Items.Where(CodeInfoResults.IsFound)
                .Where(x => x.CisInfo?.Status is { } status && status.Length <= 40 &&
                    status.All(c => char.IsAsciiLetterUpper(c) || c == '_'))
                .Select(x => new BulkCodeEntry(x.CisInfo!.Status!, x.CisInfo.RequestedCis!, x.CisInfo.OwnerInn, OwnerKnown: true)).ToArray();
            fileOperation = true;
            await files.WriteOwnedFragmentAsync(job.Id, batch.Number, entries, found, result.ErrorCount, cancellationToken);
            fileOperation = false;
            await CompleteBatchAsync(job.Id, batch.Number, found, result.ErrorCount, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (ApiDispatchNotStartedException.IsPresent(exception))
        {
            // A client timeout can also cancel acquisition before dispatch. Do not
            // silently reschedule it forever while the durable job is still Running.
            await jobs.TransitionStateAsync(job.ProfileId, job.Id, job.Mode, ["Queued", "Running"], "PauseRequested",
                "Запрос не был отправлен. Проверьте подключение и продолжите задание вручную.", cancellationToken);
        }
        catch (IOException) when (fileOperation) { throw; }
        catch (Exception exception)
        {
            await FailBatchAsync(job.Id, batch.Number,
                exception is IOException ? "Ошибка диска." : exception is InvalidDataException ? exception.Message : "Ошибка запроса к ЧЗ.", cancellationToken);
        }
    }

    private async Task CompleteBatchAsync(Guid jobId, long number, int found, int errors, CancellationToken cancellationToken)
    {
        await _batchPersistenceGate.WaitAsync(cancellationToken);
        try { await jobs.CompleteBatchAsync(jobId, number, found, errors, cancellationToken); }
        finally { _batchPersistenceGate.Release(); }
    }

    private async Task FailBatchAsync(Guid jobId, long number, string error, CancellationToken cancellationToken)
    {
        await _batchPersistenceGate.WaitAsync(cancellationToken);
        try { await jobs.FailBatchAsync(jobId, number, error, cancellationToken); }
        finally { _batchPersistenceGate.Release(); }
    }

    private async Task<string[]> ReadCodesAsync(Guid jobId, BulkCodeBatch batch, CancellationToken cancellationToken)
    {
        await using var raw = files.OpenSource(jobId, batch.StartOffset);
        var reader = new BoundedLineReader(raw);
        var codes = new List<string>(batch.CodeCount);
        var firstLine = true;
        while (reader.Position < batch.EndOffset && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (batch.StartOffset == 0 && firstLine && line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            firstLine = false;
            var code = MarkingCodeInput.TrimAfterGroupSeparator(line);
            if (reader.Error is null && ValidationError(code) is null) codes.Add(code);
        }
        return codes.ToArray();
    }

    private sealed class BoundedLineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[65536];
        private readonly byte[] _line = new byte[512];
        private int _index;
        private int _count;
        public long Position { get; private set; } = stream.Position;
        public string? Error { get; private set; }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var length = 0;
            Error = null;
            var consumed = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_index == _count)
                {
                    _count = await stream.ReadAsync(_buffer, cancellationToken);
                    _index = 0;
                    if (_count == 0) break;
                }
                var next = _buffer[_index++];
                consumed = true;
                Position++;
                if (next == '\n') break;
                if (length == _line.Length) { Error = "Строка слишком длинная; значение сокращено."; continue; }
                _line[length++] = next;
            }
            if (!consumed) return null;
            if (length > 0 && _line[length - 1] == '\r') length--;
            try { return Utf8.GetString(_line, 0, length); }
            catch (DecoderFallbackException) { Error = "Строка должна быть в UTF-8."; return Encoding.UTF8.GetString(_line, 0, length); }
        }
    }

    private static string? ValidationError(string code)
    {
        if (!CodeCharacters.IsMatch(code) ||
            MarkingCodeFormat.HasBracketedApplicationIdentifiers(code))
            return "КИ должен содержать 18–74 допустимых символа без скобок вокруг 01 и 21.";
        return null;
    }
}
