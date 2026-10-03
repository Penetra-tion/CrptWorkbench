using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record SuzUtilisationGroup(string Code, string Name, int Id, bool Automatic, string Help);
public sealed record SuzUtilisationOption(string Code, string Name);
public sealed record SuzUtilisationField(string Code, string Name, string InputType, bool Required, string Help,
    IReadOnlyList<SuzUtilisationOption>? Options = null);

/// <summary>SUZ Cloud API 3.0 §§4.4.11.1.1–43, 5.3.1.16 and 8. No online preflight.</summary>
public static class SuzUtilisationRules
{
    private static readonly HashSet<string> Automatic = Set("lp shoes perfumery tires electronics");
    private static readonly HashSet<string> Sets = Set("milk wheelchairs bio antiseptic nabeer seafood petfood toys radio vegetableoil chemistry conserve fire books grocery autofluids polymer sweets carparts gadgets meat homeware industrial fertilizers frozen furslp pyrotechnics");
    internal static readonly HashSet<string> Expiry = Set("water beer bio nabeer wheelchairs softdrinks antiseptic seafood petfood vegetableoil chemistry conserve vetpharma toys construction fire grocery autofluids sweets meat vetbio fertilizers frozen pyrotechnics");
    internal static readonly HashSet<string> VariableExpiry = Set("milk vegetableoil grocery sweets meat frozen");
    internal static readonly HashSet<string> ConditionalExpiry = Set("wheelchairs chemistry toys fire");
    internal static readonly HashSet<string> Weight = Set("milk seafood petfood sweets meat frozen");
    public static IReadOnlyList<SuzUtilisationGroup> Groups { get; } = BuildGroups();

    public static IReadOnlyList<SuzUtilisationOption> PackageTypes(string group)
    {
        if (!Groups.Any(x => x.Code == group && !x.Automatic)) return [];
        var result = new List<SuzUtilisationOption> { new("UNIT", "Потребительская упаковка") };
        if (group is not ("furslp" or "titan" or "books" or "grocery" or "nicotindev")) result.Add(new("GROUP", "Групповая упаковка"));
        if (Sets.Contains(group)) result.Add(new("SET", "Набор"));
        return result;
    }

    public static IReadOnlyList<SuzUtilisationOption> Scenarios(string group)
    {
        if (!Groups.Any(x => x.Code == group && !x.Automatic)) return [];
        var result = new List<SuzUtilisationOption> { new("UTILISATION", "Нанесение") };
        if (group == "beer") result.Add(new("RESORT", "Пересорт"));
        if (group is "radio" or "chemistry" or "toys") result.Add(new("SPLITMARK", "Из расформированного набора"));
        if (group == "radio") result.Add(new("DIVISIONMARK", "Разделение товаров"));
        if (group == "chemistry") result.Add(new("REMARK", "Перемаркировка"));
        return result;
    }

    public static IReadOnlyList<SuzUtilisationField> Fields(string group, string packageType, string scenario)
    {
        if (!PackageTypes(group).Any(x => x.Code == packageType) || !Scenarios(group).Any(x => x.Code == scenario)) return [];
        var result = new List<SuzUtilisationField>();
        void Add(string code, bool required = false) => result.Add(Field(code, required));
        if (scenario != "UTILISATION")
        {
            Add("participantId", group == "beer");
            if (group == "beer") { Add("_participantKind", true); Add("kpp"); Add("fiasId"); }
            return result;
        }
        var unit = packageType == "UNIT";
        var dates = unit;
        if (dates)
        {
            Add("productionDate", unit && group is not ("water" or "titan" or "opticfiber" or "construction"));
            if (group is "tobacco" or "otp" or "ncp") result[^1] = result[^1] with { InputType = "text" };
            if (group == "milk") { Add("expDate"); Add("expDate72"); Add("expDateConditions"); }
            else if (Expiry.Contains(group)) Add("expirationDate", unit && !ConditionalExpiry.Contains(group) && !VariableExpiry.Contains(group) && group != "construction");
            if (VariableExpiry.Contains(group) && group != "milk") Add("expDateConditions");
        }
        if (unit && ConditionalExpiry.Contains(group)) Add("_hasShelfLife", true);
        if (unit && group == "construction") Add("_cementWarranty", true);
        if (group is "milk" or "water") Add("usedInProduction");
        if (group == "milk" && unit) Add("batchNumber");
        if (group is "tobacco" or "otp" or "ncp")
        {
            foreach (var code in new[] { "productionOrderId", "brandcode", "sourceReportId", "productionLineId" }) Add(code);
        }
        if (group is "tobacco" or "otp" or "ncp" or "beer" or "nabeer" or "softdrinks" or "radio" or "construction" or "nicotindev" or "gadgets")
        {
            Add("_participantKind", group is not ("nicotindev" or "gadgets")); Add("kpp"); Add("fiasId");
        }
        if (group is "beer" or "softdrinks" or "radio" or "chemistry" or "toys") Add("participantId", group is "beer" or "softdrinks");
        if (group == "beer") { Add("alcoholVolume", true); Add("_producedInRussia", true); Add("documentNumber"); Add("documentDate"); }
        if (group == "chemistry" && (unit || dates)) Add("alcoholVolume", unit);
        if (group == "bio") Add("vetisGuid");
        if (group == "wheelchairs") { Add("batchNumber"); Add("seriesNumber"); }
        if (group == "vetpharma") Add("seriesNumber", unit);
        if (group == "titan") { Add("documentNumber", true); Add("documentDate", true); Add("documentSource", true); }
        if (unit && group is "seafood" or "petfood" or "sweets" or "meat" or "frozen") Add("_variableWeight", true);
        return result;
    }

    public static IReadOnlyList<SuzUtilisationField> IndividualFields(string group, string packageType = "UNIT")
    {
        if (packageType != "UNIT" && group != "radio") return [];
        var result = new List<SuzUtilisationField>();
        if (Weight.Contains(group)) result.Add(new("3103", "Вес, кг (AI 3103)", "text", false, "Ровно 6 цифр, без точки: 001324 = 1,324 кг."));
        if (group is "beer" or "nabeer" or "radio")
            for (var i = 0; i <= 5; i++) result.Add(Ai("335" + i, "Объём, л", i));
        if (group == "radio")
        {
            result.Add(new("30", "Количество, шт. (AI 30)", "text", false, "От 1 до 99999999. Только одна переменная характеристика на код."));
            for (var i = 0; i <= 5; i++) { result.Add(Ai("310" + i, "Вес, кг", i)); result.Add(Ai("311" + i, "Длина, м", i)); }
        }
        if (group == "wheelchairs") result.Add(new("97", "Заводской серийный номер", "text", false, "От 1 до 30 символов. Программа кодирует значение в Base64 (AI 97)."));
        return result;
    }

    internal static HashSet<string> Set(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    private static SuzUtilisationField Ai(string code, string name, int scale) => new(code, $"{name} (AI {code})", "text", false,
        $"Ровно 6 цифр, без точки; {scale} знаков дробной части. Только одна переменная характеристика на код.");

    private static SuzUtilisationField Field(string code, bool required)
    {
        var (name, type, help) = code switch
        {
            "productionDate" => ("Дата производства", "date", "Не позднее текущей даты. Табак: yyyy-MM-ddTHH:mm:ss±hhmm."),
            "expirationDate" or "expDate" => ("Дата окончания срока годности", "date", "Не ранее даты производства."),
            "expDate72" => ("Срок годности менее 72 часов", "datetime-local", "yyyy-MM-ddTHH:mm."),
            "expDateConditions" => ("Вариативные сроки годности", "textarea", "Индекс условия хранения из НКМТ и дата yyyy-MM-dd; уникальные индексы, даты не ранее текущей. Один GTIN на отчёт."),
            "_hasShelfLife" => ("В карточке товара указан срок годности", "select", "Для косметики отсутствие характеристики срока использования также требует дату окончания."),
            "_producedInRussia" => ("Продукция произведена в России", "select", "Для пива обязательны номер и дата первичного учётного документа."),
            "_variableWeight" => ("В карточке товара указан переменный вес", "select", "Вес допустим только при наличии этого признака."),
            "_cementWarranty" => ("ТН ВЭД 2523 и в карточке указан гарантийный срок", "select", "Для такой строительной продукции даты производства и годности не передаются."),
            "_participantKind" => ("Тип участника", "select", "Юридическое лицо: КПП; физическое лицо: ФИАС."),
            "participantId" => ("ИНН участника", "text", "10 или 12 цифр; контрольные цифры не проверяются."),
            "kpp" => ("КПП места деятельности", "text", "9 цифр для юридического лица."),
            "fiasId" => ("Идентификатор ФИАС", "text", "GUID места деятельности для физического лица."),
            "batchNumber" => ("Номер партии", "text", "Молоко: GTIN (14 цифр) + дата ГГММДД + 1–12 символов латиницы, цифр /.,-. С 01.11.2026 обязателен при сроке более 40 суток, с 01.03.2027 при сроке менее 40 суток."),
            "seriesNumber" => ("Номер серии", "text", "Медизделия: 1–30 символов; буквы, цифры, пробел и /.,-."),
            "usedInProduction" => ("Коды использованы на производстве", "select", "0 — нет; 1 — да."),
            "alcoholVolume" => ("Содержание этилового спирта, %", "text", "Целое число или один знак после точки. При отсутствии спирта — 0."),
            "documentNumber" => ("Номер документа", "text", "От 1 до 255 символов."),
            "documentDate" => ("Дата документа", "date", "yyyy-MM-dd."),
            "documentSource" => ("Кем выдан документ", "text", "От 1 до 255 символов."),
            "productionOrderId" => ("Производственный заказ", "text", "1–256 печатных символов."),
            "productionLineId" => ("Производственная линия", "text", "Устаревший необязательный атрибут; 1–256 символов."),
            "brandcode" => ("Бренд продукции", "text", "1–256 символов."),
            "sourceReportId" => ("Идентификатор отчёта АСУТП", "text", "GUID."),
            "vetisGuid" => ("Идентификатор ВетИС", "text", "GUID."),
            _ => (code, "text", "")
        };
        IReadOnlyList<SuzUtilisationOption>? options = type != "select" ? null : code == "_participantKind"
            ? [new("LEGAL", "Юридическое лицо"), new("PERSON", "Физическое лицо")]
            : [new("0", "Нет"), new("1", "Да")];
        return new(code, name, type, required, help, options);
    }

    private static IReadOnlyList<SuzUtilisationGroup> BuildGroups()
    {
        var groups = CodeCorrectionRules.Groups.Where(x => x.Code != "furs").Select(x => new SuzUtilisationGroup(x.Code, x.Name, x.Id,
            Automatic.Contains(x.Code), Automatic.Contains(x.Code) ? "Отчёт о нанесении формируется СУЗ автоматически; ручная отправка не требуется." : "СУЗ API 3.0, раздел 4.4.11.")).ToList();
        foreach (var (code, name, id) in new (string, string, int)[] {
            ("titan", "Титановая металлопродукция",31), ("opticfiber","Оптоволокно и оптоволоконная продукция",34),
            ("books","Печатная продукция",36), ("heater","Отопительные приборы",41), ("cableraw","Кабельно-проводниковая продукция",42),
            ("polymer","Полимерные трубы",44), ("nicotindev","Электронные системы доставки никотина",50),
            ("fertilizers","Удобрения в потребительской упаковке",53), ("homeware","Товары для дома и интерьера",54),
            ("vetbio","Кормовые добавки",55), ("industrial","Промышленное оборудование",57) })
            groups.Add(new(code,name,id,false,"СУЗ API 3.0, раздел 4.4.11."));
        return groups.OrderBy(x => x.Name, StringComparer.CurrentCulture).ToArray();
    }
}
