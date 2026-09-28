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

    public static string WithdrawalReasonLabel(string? reason) => reason switch
    {
        "AUTO_CANCELLATION" => "Аннулирование неиспользованных кодов по истечении срока",
        "BALANCE_CORRECTION" => "Корректировка остатков",
        "BEFORE_MARKING" => "Выбытие до обязательной маркировки",
        "BEYOND_EEC_EXPORT" => "Экспорт за пределы ЕАЭС",
        "BY_SAMPLES" => "Продажа по образцам",
        "CAR_SERVICE_USE" => "Использование при техобслуживании или ремонте",
        "COMPLAINTS" => "Рекламация",
        "CONFISCATION" => "Конфискация",
        "DAMAGE_LOSS" or "KM_LOST" or "LOSS" => "Утрата",
        "DEFECT" => "Повреждение или брак",
        "DEMO_SAMPLES" => "Демонстрационные образцы",
        "DESCRIPTION_ERRORS" => "Ошибки в описании товара",
        "DESTRUCTION" or "KM_DESTROYED" => "Уничтожение",
        "DISTANCE" => "Дистанционная продажа",
        "DONATION" => "Безвозмездная передача",
        "EAS_TRADE" => "Продажа в страны ЕАЭС",
        "EXPIRATION" or "EXPIRY" => "Истечение срока годности",
        "KM_CANCELLATION" => "Аннулирование неиспользованных кодов",
        "KM_CANCELATION" => "Списание при трансформации",
        "DISAGGREGATION" => "Списание упаковки после извлечения вложений",
        "KM_SPOILED" or "KM_SPOILED_OR_LOST" => "Испорчено или утеряно средство идентификации",
        "LIQUIDATION" => "Ликвидация",
        "MEDICAL_USE" => "Использование для медицинских целей",
        "MISMATCH" => "Пересортица по кодам",
        "OTHER" => "Другое",
        "OWN_USE" => "Использование для собственных нужд",
        "PACKING" => "Фасовка",
        "PRODUCT_RECALL" => "Отзыв с рынка",
        "PRODUCTION_USE" => "Использование для производственных целей",
        null or "" => "Не указана оператором",
        _ => reason
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
