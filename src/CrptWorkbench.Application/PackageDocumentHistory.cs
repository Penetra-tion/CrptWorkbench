using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed class PackageDocumentHistory(IApiRequestLogRepository logs, ISensitiveDataMasker masker, TimeProvider clock)
{
    public Task RecordAsync(PackageDocument document, IntegrationRequest request, IntegrationExchange? exchange,
        string? error, long durationMs, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return logs.AddAsync(new ApiRequestLog
        {
            CreatedAt = now, CreatedAtUnixMs = now.ToUnixTimeMilliseconds(), ProfileId = document.ProfileId,
            Environment = CrptEnvironment.Production, System = ApiSystem.TrueApi, OperationId = request.OperationId,
            CorrelationId = request.CorrelationId, HttpMethod = request.Method, Url = request.Url,
            StatusCode = exchange?.StatusCode, DurationMs = durationMs,
            RequestHeaders = masker.MaskHeaders(JsonSerializer.Serialize(exchange?.RequestHeaders ?? request.Headers)),
            RequestBody = masker.MaskBody(exchange?.RequestBody ?? request.Body),
            ResponseHeaders = exchange is null ? null : masker.MaskHeaders(JsonSerializer.Serialize(exchange.ResponseHeaders)),
            ResponseBody = masker.MaskBody(exchange?.ResponseBody),
            Success = error is null && exchange?.StatusCode is >= 200 and < 300,
            Error = masker.MaskBody(error)
        }, cancellationToken);
    }
}
