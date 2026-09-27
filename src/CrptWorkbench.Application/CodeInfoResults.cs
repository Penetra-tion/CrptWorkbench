using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record CodeInfoResults(IReadOnlyList<CodeInfoItem> Items)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public int FoundCount => Items.Count(IsFound);
    public int ErrorCount => Items.Count - FoundCount;
    public static bool IsFound(CodeInfoItem item) => item.CisInfo is not null &&
        string.IsNullOrEmpty(item.ErrorCode) && string.IsNullOrEmpty(item.ErrorMessage);

    public static CodeInfoResults? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var items = JsonSerializer.Deserialize<CodeInfoItem?[]>(body, JsonOptions);
            return items is { Length: > 0 } && items.All(x => x is not null)
                ? new(items.Select(x => x!).ToArray()) : null;
        }
        catch (JsonException) { return null; }
    }

    public static string StatusLabel(string? status) => status switch
    {
        "EMITTED" => "Эмитирован",
        "APPLIED" => "Нанесён",
        "INTRODUCED" => "В обороте",
        "RETIRED" => "Выведен из оборота",
        "WITHDRAWN" => "Выведен из оборота",
        "WRITTEN_OFF" => "Списан",
        "DISAGGREGATION" => "Расформирован",
        "DISAGGREGATED" => "Расформирован",
        "APPLIED_NOT_PAID" => "Не оплачен",
        null or "" => "—",
        _ => status
    };

    public static string? PackageTypeCode(CisInfo? info) => info?.GeneralPackageType ?? info?.PackageType;

    public static string PackageTypeLabel(CisInfo? info) => PackageTypeCode(info) switch
    {
        "UNIT" => "Единица товара",
        "GROUP" => "Групповая упаковка",
        "SET" => "Набор",
        "BUNDLE" => "Комплект",
        "BOX" => "Транспортная упаковка",
        "ATK" => "Агрегированный таможенный код",
        "LEVEL1" => "Транспортная упаковка 1-го уровня",
        "LEVEL2" => "Транспортная упаковка 2-го уровня",
        "LEVEL3" => "Транспортная упаковка 3-го уровня",
        "LEVEL4" => "Транспортная упаковка 4-го уровня",
        "LEVEL5" => "Транспортная упаковка 5-го уровня",
        null or "" => "—",
        var value => value
    };

    public static string PermitDocumentTypeLabel(string? type) => type switch
    {
        "CONFORMITY_CERTIFICATE" => "Сертификат соответствия",
        "CONFORMITY_DECLARATION" => "Декларация о соответствии",
        "STATE_REGISTRATION_CERTIFICATE" => "Свидетельство о государственной регистрации",
        "REGISTRATION_VET_CERTIFICATE" => "Регистрационное удостоверение ветеринарного препарата",
        null or "" => "Документ",
        _ => type
    };

    public static string PermitStatusGroupLabel(int? statusGroup) => statusGroup switch
    {
        1 => "Зелёный статус",
        2 => "Жёлтый статус",
        3 => "Красный статус",
        4 => "Неизвестный статус",
        null => "Статус отсутствует",
        var value => $"Группа {value}"
    };
}
