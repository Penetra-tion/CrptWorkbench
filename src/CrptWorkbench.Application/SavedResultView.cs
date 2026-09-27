using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record SavedResultField(string Label, string Value);

public sealed record SavedResultView(
    CodeInfoResults? Codes,
    NationalCatalogProductResult? Product,
    DocumentStatusResults? Documents,
    IReadOnlyList<SavedResultField> Fields,
    string? Notice)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SavedResultView From(ApiRequestLog log)
    {
        if (string.IsNullOrWhiteSpace(log.ResponseBody))
            return new(null, null, null, [], log.Error ?? "Ответ не сохранён. Откройте технический вид для подробностей.");

        try
        {
            return log.OperationId switch
            {
                TrueApiCodeInfoHandler.OperationId => FromCodes(log),
                NationalCatalogProductHandler.OperationId => FromProduct(log),
                TrueApiDocumentStatusHandler.OperationId => FromDocuments(log),
                "suz.order-status" => FromSuz(log),
                _ => new(null, null, null, [], "Для этой операции нет обычного представления. Откройте технический вид.")
            };
        }
        catch (JsonException)
        {
            return new(null, null, null, [], "Не удалось прочитать сохранённый ответ. Откройте технический вид.");
        }
    }

    private static SavedResultView FromCodes(ApiRequestLog log)
    {
        var result = CodeInfoResults.Parse(log.ResponseBody);
        return result is null ? Invalid() : new(result, null, null, [], null);
    }

    private static SavedResultView FromProduct(ApiRequestLog log)
    {
        var result = JsonSerializer.Deserialize<NationalCatalogProductResult>(log.ResponseBody!, JsonOptions);
        return result is null || string.IsNullOrWhiteSpace(result.Gtin) || result.Packages is null || result.Documents is null
            ? Invalid() : new(null, result, null, [], null);
    }

    private static SavedResultView FromDocuments(ApiRequestLog log)
    {
        var result = DocumentStatusResults.Parse(log.ResponseBody);
        return result is null ? Invalid() : new(null, null, result, [], null);
    }

    private static SavedResultView FromSuz(ApiRequestLog log)
    {
        using var document = JsonDocument.Parse(log.ResponseBody!);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
        var fields = document.RootElement.EnumerateObject()
            .Where(x => x.Name is "id" or "status" or "system" or "message")
            .Select(x => new SavedResultField(x.Name switch
            {
                "id" => "Идентификатор заказа",
                "status" => "Статус",
                "system" => "Система",
                _ => "Комментарий"
            }, x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "—" : x.Value.ToString()))
            .ToArray();
        return fields.Length == 0 ? Invalid() : new(null, null, null, fields, null);
    }

    private static SavedResultView Invalid() => new(null, null, null, [], "Не удалось прочитать сохранённый ответ. Откройте технический вид.");
}
