using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record DemoLookupRequest(string Value);
public sealed record DemoLookupResponse(string Id, string Status, string System, string Message);

public abstract class DemoOperationHandler
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    protected static IntegrationRequest CreateRequest(ApiOperationDescriptor descriptor, ApiOperationContext context, IReadOnlyDictionary<string, string> values, string baseUrl)
    {
        InputValidation.Operation(descriptor.Id, values).ThrowIfInvalid();
        var value = values.GetValueOrDefault("value")?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The operation value is required.");
        }

        return new IntegrationRequest(
            descriptor.Id,
            descriptor.HttpMethod,
            $"{baseUrl.TrimEnd('/')}/mock/{descriptor.Id}?value={Uri.EscapeDataString(value)}",
            new Dictionary<string, string>
            {
                ["X-Correlation-Id"] = context.CorrelationId,
                ["Authorization"] = "Bearer mock-token-not-a-real-secret"
            },
            null,
            context.CorrelationId);
    }

    protected static string ResolveBaseUrl(string? configured, ApiSystem system)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return $"https://production.{system.ToString().ToLowerInvariant()}.mock.invalid";
    }
}

public sealed class SuzDemoHandler(ISuzClient client) : DemoOperationHandler, IApiOperationHandler
{
    public ApiOperationDescriptor Descriptor { get; } = new("suz.order-status", "Запрос статуса заказа · Mock", ApiSystem.Suz, "GET",
        "Проверка статуса заказа СУЗ. Сейчас ответ формируется локально.", typeof(DemoLookupRequest), typeof(DemoLookupResponse),
        [new("value", "Order ID", "demo-order-001")]);

    public Task<IntegrationExchange> ExecuteAsync(IReadOnlyDictionary<string, string> values, ApiOperationContext context, CancellationToken cancellationToken) =>
        client.ExecuteAsync(CreateRequest(Descriptor, context, values, ResolveBaseUrl(context.Profile.SuzProductionBaseUrl, Descriptor.System)), cancellationToken);
}
