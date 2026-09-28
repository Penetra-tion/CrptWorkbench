using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record ApiFieldDescriptor(string Name, string Label, string? Placeholder = null, bool Required = true,
    bool Multiline = false, string? HelpText = null, string? CheckedValue = null);

public sealed record ApiOperationDescriptor(
    string Id,
    string Name,
    ApiSystem System,
    string HttpMethod,
    string Description,
    Type RequestType,
    Type? ResponseType,
    IReadOnlyList<ApiFieldDescriptor> Fields);

public sealed record ApiOperationContext(CrptProfile Profile, string CorrelationId);

public sealed record ApiError(string Message, string? Code = null, string? Details = null);

public sealed record ApiHttpExchange(
    int StatusCode,
    TimeSpan Duration,
    string HttpMethod,
    string RequestUrl,
    string RequestHeaders,
    string? RequestBody,
    string ResponseHeaders,
    string? ResponseBody);

public sealed record ApiExecutionResult(
    bool Success,
    int? StatusCode,
    string CorrelationId,
    TimeSpan Duration,
    string HttpMethod,
    string RequestUrl,
    string RequestHeaders,
    string? RequestBody,
    string ResponseHeaders,
    string? ResponseBody,
    ApiError? Error = null,
    IReadOnlyList<ApiHttpExchange>? Exchanges = null);

public interface IApiOperationHandler
{
    ApiOperationDescriptor Descriptor { get; }
    Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken);
}

public interface IApiOperationRegistry
{
    IReadOnlyList<ApiOperationDescriptor> List();
    IApiOperationHandler GetRequired(string operationId);
}

public sealed class ApiOperationRegistry(IEnumerable<IApiOperationHandler> handlers) : IApiOperationRegistry
{
    private readonly IReadOnlyDictionary<string, IApiOperationHandler> _handlers = handlers.ToDictionary(x => x.Descriptor.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ApiOperationDescriptor> List() => _handlers.Values.Select(x => x.Descriptor).OrderBy(x => x.System).ToArray();

    public IApiOperationHandler GetRequired(string operationId) =>
        _handlers.TryGetValue(operationId, out var handler)
            ? handler
            : throw new KeyNotFoundException($"Operation '{operationId}' is not registered.");
}

public sealed class WorkbenchContext
{
    private Guid? _profileId;

    public event EventHandler<Guid?>? ProfileChanged;

    public Guid? ProfileId
    {
        get => _profileId;
        set
        {
            if (_profileId == value) return;
            _profileId = value;
            ProfileChanged?.Invoke(this, value);
        }
    }

    public void NotifyProfileChanged() => ProfileChanged?.Invoke(this, _profileId);
}

public sealed class FavoriteApiOperationService(
    IFavoriteApiOperationRepository repository,
    IApiOperationRegistry registry)
{
    public async Task<IReadOnlyList<string>> ListAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var registered = registry.List().Select(operation => operation.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var saved = await repository.ListOperationIdsAsync(profileId, cancellationToken);
        return saved.Where(registered.Contains).ToArray();
    }

    public Task SetAsync(Guid profileId, string operationId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        _ = registry.GetRequired(operationId);
        return repository.SetAsync(profileId, operationId, isFavorite, cancellationToken);
    }
}

public sealed class FavoriteProductGroupService(IFavoriteApiOperationRepository repository)
{
    private const string Prefix = "package-transformation:product-group:";

    public async Task<IReadOnlyList<string>> ListAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var saved = await repository.ListOperationIdsAsync(profileId, cancellationToken);
        return saved
            .Where(x => x.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(x => x[Prefix.Length..])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public Task SetAsync(Guid profileId, string groupCode, bool isFavorite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupCode);
        return repository.SetAsync(profileId, Prefix + groupCode, isFavorite, cancellationToken);
    }
}

public sealed class ExecuteApiOperationService(
    IApiOperationRegistry registry,
    IProfileRepository profiles,
    IApiRequestLogRepository history,
    ISensitiveDataMasker masker, ILogger<ExecuteApiOperationService>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ApiExecutionResult> ExecuteAsync(
        string operationId,
        IReadOnlyDictionary<string, string> values,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (operationId.Equals(SuzDocumentStatusHandler.OperationId, StringComparison.OrdinalIgnoreCase))
            InputValidation.Operation(SuzDocumentStatusHandler.OperationId, values).ThrowIfInvalid();
        var profile = await profiles.GetAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException("Select an existing profile before running an operation.");
        var handler = registry.GetRequired(operationId);
        var correlationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var operationLogger = logger ?? NullLogger<ExecuteApiOperationService>.Instance;
        using var scope = operationLogger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        operationLogger.LogInformation("Operation started {OperationId} {ProfileId}", handler.Descriptor.Id, profileId);

        try
        {
            InputValidation.Operation(handler.Descriptor.Id, values).ThrowIfInvalid();
            var exchange = await handler.ExecuteAsync(values, new(profile, correlationId), cancellationToken);
            var requestHeaders = masker.MaskHeaders(Serialize(exchange.RequestHeaders)) ?? "{}";
            var responseHeaders = masker.MaskHeaders(Serialize(exchange.ResponseHeaders)) ?? "{}";
            var exchanges = (exchange.Exchanges ?? [exchange]).Select(ToApiExchange).ToArray();
            var invalidSuzResponse = handler.Descriptor.Id == SuzDocumentStatusHandler.OperationId &&
                exchange.StatusCode is >= 200 and < 300 && SuzDocumentStatusResults.Parse(exchange.ResponseBody) is null;
            var result = new ApiExecutionResult(
                exchange.StatusCode is >= 200 and < 300 && !invalidSuzResponse,
                exchange.StatusCode,
                correlationId,
                exchange.Duration,
                exchange.RequestMethod,
                exchange.RequestUrl,
                requestHeaders,
                masker.MaskBody(exchange.RequestBody),
                responseHeaders,
                masker.MaskBody(exchange.ResponseBody),
                invalidSuzResponse ? new ApiError("СУЗ вернул некорректную структуру квитанций. Откройте технический вид.", "invalid_response") :
                    exchange.StatusCode is >= 200 and < 300 ? null : MapHttpError(exchange.StatusCode, handler.Descriptor.System),
                exchanges);

            await history.AddAsync(ToLog(result, handler.Descriptor, profileId, startedAt), cancellationToken);
            operationLogger.LogInformation("Operation completed {OperationId} {CorrelationId} {Success} {StatusCode} {ElapsedMs}", handler.Descriptor.Id, correlationId, result.Success, result.StatusCode, result.Duration.TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            operationLogger.LogInformation("Operation canceled {OperationId} {CorrelationId}", handler.Descriptor.Id, correlationId);
            throw;
        }
        catch (Exception exception)
        {
            operationLogger.LogError("Operation failed {OperationId} {CorrelationId} {ExceptionType}", handler.Descriptor.Id, correlationId, exception.GetType().Name);
            var result = new ApiExecutionResult(false, null, correlationId, DateTimeOffset.UtcNow - startedAt, handler.Descriptor.HttpMethod, string.Empty, "{}", null, "{}", null,
                new ApiError(exception.Message, "operation_failed"));
            await history.AddAsync(ToLog(result, handler.Descriptor, profileId, startedAt), cancellationToken);
            operationLogger.LogInformation("Operation completed {OperationId} {CorrelationId} {Success} {StatusCode} {ElapsedMs}", handler.Descriptor.Id, correlationId, result.Success, result.StatusCode, result.Duration.TotalMilliseconds);
            return result;
        }
    }

    private ApiRequestLog ToLog(ApiExecutionResult result, ApiOperationDescriptor descriptor, Guid profileId, DateTimeOffset createdAt) => new()
    {
        CreatedAt = createdAt,
        CreatedAtUnixMs = createdAt.ToUnixTimeMilliseconds(),
        ProfileId = profileId,
        Environment = CrptEnvironment.Production,
        System = descriptor.System,
        OperationId = descriptor.Id,
        CorrelationId = result.CorrelationId,
        HttpMethod = result.HttpMethod,
        Url = result.RequestUrl,
        StatusCode = result.StatusCode,
        DurationMs = (long)result.Duration.TotalMilliseconds,
        RequestHeaders = masker.MaskHeaders(result.RequestHeaders),
        RequestBody = masker.MaskBody(result.RequestBody),
        ResponseHeaders = masker.MaskHeaders(result.ResponseHeaders),
        ResponseBody = masker.MaskBody(result.ResponseBody),
        Success = result.Success,
        Error = result.Error?.Message,
        ExchangesJson = result.Exchanges is { Count: > 0 } ? JsonSerializer.Serialize(result.Exchanges, JsonOptions) : null
    };

    private ApiHttpExchange ToApiExchange(IntegrationExchange exchange) => new(
        exchange.StatusCode,
        exchange.Duration,
        exchange.RequestMethod,
        exchange.RequestUrl,
        masker.MaskHeaders(Serialize(exchange.RequestHeaders)) ?? "{}",
        masker.MaskBody(exchange.RequestBody),
        masker.MaskHeaders(Serialize(exchange.ResponseHeaders)) ?? "{}",
        masker.MaskBody(exchange.ResponseBody));

    private static ApiError MapHttpError(int statusCode, ApiSystem system)
    {
        if (system != ApiSystem.NationalCatalog)
            return new ApiError($"Сервис вернул ошибку HTTP {statusCode}.", "http_error");
        return statusCode switch
        {
            401 => new("Bearer-токен Национального каталога отсутствует или истёк. Обновите его в настройках.", "unauthorized"),
            403 => new("У выбранного профиля нет доступа к этой карточке.", "forbidden"),
            404 => new("Собственная карточка с указанным GTIN не найдена. Карточки в статусе «Требует обработки» недоступны через этот метод.", "not_found"),
            429 => new("Превышен лимит запросов Национального каталога. Повторите запрос позже.", "rate_limited"),
            >= 500 => new("Национальный каталог временно недоступен. Повторите запрос позже.", "service_unavailable"),
            _ => new($"Национальный каталог вернул ошибку HTTP {statusCode}.", "http_error")
        };
    }

    private static string Serialize(IReadOnlyDictionary<string, string> headers) => JsonSerializer.Serialize(headers, JsonOptions);

}


