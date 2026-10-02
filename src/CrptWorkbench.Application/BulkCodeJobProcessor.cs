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
            while (!cancellationToken.IsCancellationRequested)
            {
                job = await jobs.GetAsync(job.ProfileId, job.Id, cancellationToken);
                if (job is null || job.State is "Canceled" or "Completed" or "Partial" or "Failed") return;
                if (job.State == "CancelRequested")
                {
                    await FinishAsync(job, "Canceled", null, cancellationToken);
                    return;
                }

                var pending = (await jobs.ListBatchesAsync(job.Id, "Queued", 4, cancellationToken)).ToList();
                while (pending.Count < 4 && job.NextOffset < job.FileBytes && job.State != "CancelRequested")
                {
                    var next = await PrepareBatchAsync(job, cancellationToken);
                    job = (await jobs.GetAsync(job.ProfileId, job.Id, cancellationToken))!;
                    if (job.State == "CancelRequested")
                    {
                        await FinishAsync(job, "Canceled", null, cancellationToken);
                        return;
                    }
                    pending.Add(next);
                }
                if (pending.Count == 0)
                {
                    await FinishAsync(job, job.FailedBatches > 0 ? "Partial" : "Completed", null, cancellationToken);
                    return;
                }
                await Parallel.ForEachAsync(pending,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4), CancellationToken = cancellationToken },
                    async (batch, token) => await ProcessBatchAsync(job, profile, batch, token));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var message = exception is IOException ? "Недостаточно места на диске или ошибка чтения файла." :
                exception is InvalidDataException or DecoderFallbackException ? exception.Message : "Ошибка обработки файла.";
            var current = await jobs.GetAsync(profileId, jobId, CancellationToken.None);
            if (current is not null && current.State is not ("Canceled" or "Completed" or "Partial" or "Failed"))
                await FinishAsync(current, current.State == "CancelRequested" ? "Canceled" : "Failed", message, CancellationToken.None);
        }
    }

    private async Task FinishAsync(BulkCodeJob job, string state, string? error, CancellationToken cancellationToken)
    {
        var current = await jobs.GetAsync(job.ProfileId, job.Id, cancellationToken) ?? job;
        await jobs.SetJobStateAsync(job.ProfileId, job.Id, state, error, cancellationToken);
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
        while (count < 1000 && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines++;
            if (line.Length == 0) continue;
            if (job.NextOffset == 0 && lines == 1 && line[0] == '\uFEFF') line = line[1..];
            ValidateCode(MarkingCodeInput.TrimAfterGroupSeparator(line), job.LinesRead + lines);
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
                new(profile.Id, profile.Inn, profile.CertificateThumbprint));
            IntegrationExchange? exchange = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                exchange = await client.ExecuteAsync(request, cancellationToken);
                if (exchange.StatusCode != 429 || attempt == 2) break;
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
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
                .Select(x => (x.CisInfo!.Status!, x.CisInfo.RequestedCis!)).ToArray();
            fileOperation = true;
            await files.WriteFragmentAsync(job.Id, batch.Number, entries, found, result.ErrorCount, cancellationToken);
            fileOperation = false;
            await CompleteBatchAsync(job.Id, batch.Number, found, result.ErrorCount, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
        while (reader.Position < batch.EndOffset && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (batch.StartOffset == 0 && codes.Count == 0 && line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            if (line.Length > 0) codes.Add(MarkingCodeInput.TrimAfterGroupSeparator(line));
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

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var length = 0;
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
                Position++;
                if (next == '\n') break;
                if (length == _line.Length) throw new InvalidDataException("Код в файле длиннее допустимых 74 символов.");
                _line[length++] = next;
            }
            if (length == 0 && _index == _count && Position == stream.Length) return null;
            if (length > 0 && _line[length - 1] == '\r') length--;
            try { return Utf8.GetString(_line, 0, length); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Файл должен быть в UTF-8."); }
        }
    }

    private static void ValidateCode(string code, long line)
    {
        if (!CodeCharacters.IsMatch(code) ||
            code.Contains("(01)", StringComparison.Ordinal) || code.Contains("(21)", StringComparison.Ordinal))
            throw new InvalidDataException($"Строка {line}: КИ должен содержать 18–74 допустимых символа без скобок вокруг 01 и 21.");
    }
}
