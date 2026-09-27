using System.Globalization;
using System.Text.Json;

namespace CrptWorkbench.Application;

public sealed record DocumentStatusResults(IReadOnlyList<DocumentStatusItem> Items)
{
    public static DocumentStatusResults? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var items = new List<DocumentStatusItem>();
            foreach (var value in document.RootElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object) return null;
                var date = String(value, "receivedAt");
                items.Add(new DocumentStatusItem(String(value, "number"),
                    DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var received) ? received.ToUniversalTime() : null,
                    String(value, "type"), Strings(value, "productGroup"), String(value, "status"),
                    Strings(value, "errors"), Errors(value)));
            }
            return new(items);
        }
        catch (JsonException) { return null; }
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static IReadOnlyList<string>? Strings(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Array
            ? field.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : null;

    private static IReadOnlyList<DocumentStatusError>? Errors(JsonElement value) =>
        value.TryGetProperty("commonErrors", out var field) && field.ValueKind == JsonValueKind.Array
            ? field.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new DocumentStatusError(String(x, "errorMessage"))).ToArray()
            : null;

    public static IReadOnlyList<string> ErrorTexts(DocumentStatusItem item) =>
        (item.Errors ?? []).Concat((item.CommonErrors ?? []).Select(x => x.ErrorMessage ?? ""))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();

    public static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "Не указано" : value;
    public static string ReceivedAt(DateTimeOffset? value) => value?.ToUniversalTime().ToString("dd.MM.yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Не указано";

    public static string Type(string? code) => string.IsNullOrWhiteSpace(code) ? "Не указано" : code switch
    {
        "LP_INTRODUCE_GOODS" => "Ввод в оборот. Производство РФ",
        "LP_INTRODUCE_GOODS_CSV" or "LP_INTRODUCE_GOODS_XML" => "Ввод в оборот. Производство РФ",
        "LP_SHIP_GOODS" or "LP_SHIP_GOODS_CSV" or "LP_SHIP_GOODS_XML" => "Отгрузка",
        "LP_ACCEPT_GOODS" or "LP_ACCEPT_GOODS_XML" => "Приёмка",
        "AGGREGATION_DOCUMENT" or "AGGREGATION_DOCUMENT_XML" => "Формирование упаковки",
        "SETS_AGGREGATION" or "SETS_AGGREGATION_XML" => "Формирование набора",
        "DISAGGREGATION_DOCUMENT" or "DISAGGREGATION_DOCUMENT_XML" => "Расформирование упаковки",
        "REAGGREGATION_DOCUMENT" => "Трансформация упаковки",
        "EAS_GTIN_CROSSBORDER_EXPORT" or "EAS_GTIN_CROSSBORDER_EXPORT_CSV" => "Отгрузка в ЕАЭС (ОСУ)",
        "REPORT_REWEIGHING" => "Отчёт о перевзвешивании",
        "EAS_GTIN_CROSSBORDER_ACCEPTANCE" or "EAS_GTIN_CROSSBORDER_ACCEPTANCE_CSV" => "Приёмка из ЕАЭС (ОСУ)",
        "LK_REMARK" or "LK_REMARK_XML" => "Перемаркировка",
        "LP_GOODS_IMPORT" => "Ввод в оборот. Производство вне ЕАЭС",
        "LP_CANCEL_SHIPMENT" => "Отмена отгрузки",
        "LK_KM_CANCELLATION" => "Списание",
        "LK_APPLIED_KM_CANCELLATION" => "Списание под последующую перемаркировку",
        "LK_CONTRACT_COMMISSIONING" or "LK_CONTRACT_COMMISSIONING_XML" => "Ввод в оборот. Контрактное производство",
        "LK_INDI_COMMISSIONING" or "LK_INDI_COMMISSIONING_XML" => "Ввод в оборот. Получено от физических лиц",
        "LP_RETURN" or "LP_RETURN_XML" => "Возврат в оборот",
        "LP_INTRODUCE_OST" => "Ввод в оборот. Остатки",
        "CROSSBORDER" or "FURS_CROSSBORDER" => "Ввод в оборот. Трансграничная торговля",
        "LK_RECEIPT" or "LK_RECEIPT_CSV" or "LK_RECEIPT_XML" => "Вывод из оборота",
        "LK_RECEIPT_CANCEL" => "Отмена вывода из оборота",
        "LP_FTS_INTRODUCE" or "LP_FTS_INTRODUCE_XML" or "FURS_FTS_INTRODUCE" => "Ввод в оборот. Импорт с ФТС",
        "ATK_AGGREGATION" or "ATK_AGGREGATION_XML" => "Формирование АТК",
        "ATK_TRANSFORMATION" => "Трансформация АТК",
        "ATK_DISAGGREGATION" => "Расформирование АТК",
        "WRITE_OFF" => "Списание",
        "ALCO_UTILISED" => "Отчёт о нанесении ФСМ",
        "EAS_CROSSBORDER_EXPORT" or "EAS_CROSSBORDER_EXPORT_CSV" => "Отгрузка в ЕАЭС",
        "LK_INDIVIDUALIZATION" or "LK_INDIVIDUALIZATION_XML" => "Индивидуализация КиЗ",
        "LK_GTIN_RECEIPT" => "Вывод из оборота (ОСУ)",
        "LK_GTIN_RECEIPT_CANCEL" => "Отмена вывода из оборота (ОСУ)",
        "CIS_INFORMATION_CHANGE" => "Корректировка сведений о кодах",
        "CIS_NOTICE" => "Уведомление о состоянии кодов",
        "CONNECT_TAP" => "Подключение кега к оборудованию для розлива",
        "UNIVERSAL_TRANSFER_DOCUMENT" => "УПД",
        "UNIVERSAL_TRANSFER_DOCUMENT_FIX" => "УПДи",
        "UNIVERSAL_CORRECTION_DOCUMENT" => "УКД",
        "UNIVERSAL_CORRECTION_DOCUMENT_FIX" => "УКДи",
        "FIXATION" => "УПД. Отгрузка продукции",
        "FIXATION_CANCEL" => "Отмена отгрузки по УПД",
        _ => "Неизвестный тип документа"
    };

    public static string Status(string? code) => Label(code, code switch
    {
        "IN_PROGRESS" or "WAIT_FOR_CONTINUATION" => "Проверяется",
        "CHECKED_OK" => "Обработан",
        "CHECKED_NOT_OK" or "PARSE_ERROR" => "Обработан с ошибками",
        "PROCESSING_ERROR" => "Техническая ошибка",
        "ACCEPTED" => "Принят",
        "CANCELLED" => "Аннулирован",
        "WAIT_ACCEPTANCE" => "Ожидает приёмку",
        "WAIT_PARTICIPANT_REGISTRATION" => "Ожидает регистрации участника в ГИС МТ",
        _ => null
    });

    public static string Group(string? code) => Label(code, code switch
    {
        "lp" => "Предметы одежды и бельё",
        "shoes" => "Обувные товары",
        "tobacco" => "Табачная продукция",
        "perfumery" => "Духи и туалетная вода",
        "tires" => "Шины и покрышки",
        "electronics" => "Фотокамеры, фотовспышки и лампы-вспышки",
        "milk" => "Молочная продукция",
        "bicycle" => "Велосипеды и велосипедные рамы",
        "wheelchairs" => "Медицинские изделия",
        "alcohol" => "Слабоалкогольные напитки",
        "otp" => "Альтернативная табачная продукция",
        "water" => "Упакованная вода",
        "furs" => "Товары из натурального меха",
        "beer" => "Пиво и слабоалкогольные напитки",
        "ncp" => "Никотиносодержащая продукция",
        "bio" => "Биологически активные добавки к пище",
        "antiseptic" => "Антисептики и дезинфицирующие средства",
        "petfood" => "Корма для животных",
        "seafood" => "Морепродукты",
        "nabeer" => "Безалкогольное пиво",
        "softdrinks" => "Соковая продукция и безалкогольные напитки",
        "vetpharma" => "Ветеринарные препараты",
        "toys" => "Игры и игрушки для детей",
        "radio" => "Радиоэлектронная продукция",
        "titan" => "Титановая металлопродукция",
        "conserve" => "Консервированная продукция",
        "vegetableoil" => "Растительные масла",
        "opticfiber" => "Оптоволокно и оптоволоконная продукция",
        "chemistry" => "Парфюмерные и косметические средства и бытовая химия",
        "books" => "Печатная продукция",
        "grocery" => "Бакалейная продукция",
        "pharmaraw" => "Фармацевтическое сырьё и лекарственные средства",
        "construction" => "Строительные материалы",
        "fire" => "Пиротехника и огнетушащее оборудование",
        "heater" => "Отопительные приборы",
        "cableraw" => "Кабельно-проводниковая продукция",
        "autofluids" => "Моторные масла",
        "polymer" => "Полимерные трубы",
        "sweets" => "Сладости и кондитерские изделия",
        "carparts" => "Автозапчасти и комплектующие транспортных средств",
        _ => null
    });

    private static string Label(string? code, string? label) =>
        string.IsNullOrWhiteSpace(code) ? "Не указано" : label is null ? code : $"{label} ({code})";
}
