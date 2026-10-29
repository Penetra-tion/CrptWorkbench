namespace CrptWorkbench.Domain;

public sealed record ApiRequestLogSummary(Guid Id, DateTimeOffset CreatedAt, long CreatedAtUnixMs,
    ApiSystem System, string OperationId, int? StatusCode, long DurationMs, bool Success);
