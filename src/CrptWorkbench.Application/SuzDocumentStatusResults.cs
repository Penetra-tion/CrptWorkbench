using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrptWorkbench.Application;

public sealed record SuzReceiptEvent(
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string OperationType,
    [property: JsonRequired] long OperationDate,
    string? DocId, long? DocDate, string? DocHash, JsonElement? Details)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
}

public sealed record SuzReceipt(
    [property: JsonRequired] string ResultDocId,
    [property: JsonRequired] long ResultDocDate,
    [property: JsonRequired] string SourceDocId,
    [property: JsonRequired] long SourceDocDate,
    [property: JsonRequired] string State,
    [property: JsonRequired] int Code,
    [property: JsonRequired] string Description,
    string? Workflow, int? WorkflowVersion, JsonElement? Details, IReadOnlyList<SuzReceiptEvent>? Operations)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
}

public sealed record SuzDocumentStatusResults([property: JsonRequired] IReadOnlyList<SuzReceipt> Results)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalFields { get; init; }

    public static SuzDocumentStatusResults? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<SuzDocumentStatusResults>(body, JsonOptions);
            if (result?.Results is null || result.Results.Any(item => item is null ||
                string.IsNullOrWhiteSpace(item.ResultDocId) || string.IsNullOrWhiteSpace(item.SourceDocId) ||
                string.IsNullOrWhiteSpace(item.State) || item.Description is null ||
                !ValidDate(item.ResultDocDate) || !ValidDate(item.SourceDocDate) ||
                item.Operations?.Any(op => op is null || string.IsNullOrWhiteSpace(op.OperationId) ||
                    string.IsNullOrWhiteSpace(op.OperationType) || !ValidDate(op.OperationDate)) == true)) return null;
            return result;
        }
        catch (JsonException) { return null; }
    }

    private static bool ValidDate(long value) => value is >= -62135596800000 and <= 253402300799999;
    public static string Date(long? value) => value is not null && ValidDate(value.Value)
        ? DocumentStatusResults.ReceivedAt(DateTimeOffset.FromUnixTimeMilliseconds(value.Value)) : "Не указано";
    public static string Status(string? state) => state switch
    {
        "SUCCESS" => "Обработан успешно (SUCCESS)",
        "FAILED" => "Обработан с ошибкой (FAILED)",
        "IN_PROGRESS" => "В обработке (IN_PROGRESS)",
        "PARTIALLY" => "Обработан частично (PARTIALLY)",
        _ => DocumentStatusResults.Value(state)
    };

    public static string Workflow(string? value) => value switch
    {
        "CREATE_ORDER" => "Заказ кодов маркировки",
        "REPORT_UTILIZE" => "Отчёт о нанесении",
        "REPORT_AGGREGATION" => "Отчёт об агрегации",
        "REPORT_DROPOUT" => "Отчёт о выбытии",
        "ANNULMENT_CODES" => "Аннулирование кодов маркировки",
        "GET_CODES" => "Выдача кодов маркировки",
        "REPORT_QUALITY" => "Сведения о нанесении",
        "CLOSE_ORDER" => "Закрытие заказа",
        "REPORT_SURPLUS" => "Уведомление о принятии к учёту КИ",
        _ => DocumentStatusResults.Value(value)
    };

    public static string Operation(string? value) => value switch
    {
        "REPORT_UTILIZE_RECEIVED" => "Получение отчёта о нанесении КМ",
        "REPORT_UTILIZE_VALIDATION" => "Отправка на проверку метаданных отчёта в МДЛП",
        "REPORT_UTILIZE_VALIDATION_RESULT" => "Результат проверки метаданных отчёта в МДЛП",
        "REPORT_UTILIZE_EMISSION_SEND" => "Отправка отчёта в РЭ/РЭМ",
        "RNMS_GIS_ROUTED" => "Отправка отчёта на обработку",
        "RNMS_GIS_PROCESSED" => "Результат обработки отчёта",
        _ => DocumentStatusResults.Value(value)
    };

    public static string Json(JsonElement? value) => value is null ? "{}" : JsonSerializer.Serialize(value.Value, JsonOptions);

    public static IReadOnlyList<string> ErrorTexts(SuzReceipt item)
    {
        var errors = new List<string>();
        if (item.State is "FAILED" or "PARTIALLY" && !string.IsNullOrWhiteSpace(item.Description)) errors.Add(item.Description);
        CollectErrors(item.Details, errors);
        foreach (var operation in item.Operations ?? []) CollectErrors(operation.Details, errors);
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void CollectErrors(JsonElement? value, List<string> errors)
    {
        if (value is null) return;
        if (value.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.Value.EnumerateArray()) CollectErrors(child, errors);
        }
        else if (value.Value.ValueKind == JsonValueKind.Object)
        {
            var failed = value.Value.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String &&
                state.GetString() is "FAILED" or "REJECTED" or "PARTIALLY";
            foreach (var property in value.Value.EnumerateObject())
            {
                if (property.Name is "error" or "errorReason" or "errorMessage" or "errors" ||
                    (failed && property.Name is "description" or "message"))
                {
                    if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        errors.Add(property.Value.GetString()!);
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                        foreach (var child in property.Value.EnumerateArray())
                            if (child.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(child.GetString())) errors.Add(child.GetString()!);
                }
                CollectErrors(property.Value, errors);
            }
        }
    }
}
