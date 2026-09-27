namespace CrptWorkbench.Domain;

public sealed record CorrectionProductGroup(string Code, string Name, int Id);
public sealed record CorrectionKind(string Code, string Name);

/// <summary>True API v733.0, 25.09.2026, section 4.1.17 and referenced dictionaries.
/// Reviewed snapshot: docs/crpt-reference/cis-information-change-current.txt.</summary>
public static class CodeCorrectionRules
{
    public const string ReferenceVersion = "733.0 от 25.09.2026";

    public static IReadOnlyList<CorrectionProductGroup> Groups { get; } = Array.AsReadOnly<CorrectionProductGroup>([
        new("carparts", "Автозапчасти и комплектующие транспортных средств", 48),
        new("otp", "Альтернативная табачная продукция", 12),
        new("antiseptic", "Антисептики и дезинфицирующие средства", 19),
        new("grocery", "Бакалейная продукция", 37), new("nabeer", "Безалкогольное пиво", 22),
        new("bicycle", "Велосипеды и велосипедные рамы", 9), new("vetpharma", "Ветеринарные препараты", 26),
        new("perfumery", "Духи и туалетная вода", 4), new("toys", "Игры и игрушки для детей", 27),
        new("conserve", "Консервированная продукция", 32), new("petfood", "Корма для животных", 20),
        new("chemistry", "Косметика, бытовая химия и товары личной гигиены", 35),
        new("lp", "Лёгкая промышленность", 1), new("wheelchairs", "Медицинские изделия", 10),
        new("milk", "Молочная продукция", 8), new("seafood", "Морепродукты", 21),
        new("autofluids", "Моторные масла", 43), new("meat", "Мясные изделия", 25),
        new("furslp", "Натуральный мех", 49), new("ncp", "Никотиносодержащая продукция", 16),
        new("shoes", "Обувные товары", 2),
        new("beer", "Пиво, напитки, изготавливаемые на основе пива, слабоалкогольные напитки", 15),
        new("pyrotechnics", "Пиротехнические изделия", 59),
        new("frozen", "Полуфабрикаты и замороженные продукты", 52),
        new("radio", "Радиоэлектронная продукция", 28),
        new("gadgets", "Радиоэлектронная продукция. Ноутбуки и смартфоны", 51),
        new("vegetableoil", "Растительные масла", 33), new("sweets", "Сладости и кондитерские изделия", 45),
        new("softdrinks", "Соковая продукция и безалкогольные напитки", 23),
        new("bio", "Специализированная пищевая продукция и БАД к пище", 17),
        new("fire", "Средства обеспечения пожарной безопасности и пожаротушения", 40),
        new("construction", "Строительные материалы", 39), new("tobacco", "Табачная продукция", 3),
        new("furs", "Товары из натурального меха", 14), new("water", "Упакованная вода", 13),
        new("electronics", "Фотокамеры (кроме кинокамер), фотовспышки и лампы-вспышки", 6),
        new("tires", "Шины и покрышки пневматические резиновые новые", 5)
    ]);

    public static IReadOnlyList<CorrectionKind> Kinds { get; } = Array.AsReadOnly<CorrectionKind>([
        new("vsd", "Идентификатор ВСД"), new("productionDate", "Дата производства"),
        new("expirationDate", "Дата истечения срока годности"), new("variableExpiration", "Вариативный срок годности"),
        new("weight", "Фактический вес в граммах"), new("tnved", "Код ТН ВЭД ЕАЭС"),
        new("permitDocs", "Разрешительные документы"), new("licences", "Лицензии на пользование недрами"),
        new("emissionType", "Тип эмиссии"), new("partNumber", "Номер партии")
    ]);

    public static bool Supports(string group, string kind) => Groups.Any(x => x.Code == group) && kind switch
    {
        "productionDate" => true,
        "vsd" => group is "conserve" or "petfood" or "milk" or "seafood" or "meat" or "frozen" or "sweets",
        "expirationDate" => group is "antiseptic" or "grocery" or "nabeer" or "vetpharma" or "toys" or
            "conserve" or "petfood" or "chemistry" or "wheelchairs" or "milk" or "seafood" or "autofluids" or
            "meat" or "beer" or "pyrotechnics" or "frozen" or "vegetableoil" or "sweets" or "softdrinks" or
            "bio" or "fire" or "construction" or "water",
        "variableExpiration" => group is "milk" or "meat" or "frozen" or "sweets",
        "weight" => group is "milk" or "seafood" or "meat" or "frozen",
        "tnved" => group is not ("otp" or "nabeer" or "ncp" or "beer" or "tobacco"),
        "permitDocs" => group is not ("vetpharma" or "bio"),
        "licences" => group == "water",
        "emissionType" => group is not ("beer" or "radio" or "gadgets" or "sweets" or "furs"),
        "partNumber" => group == "milk",
        _ => false
    };

    public static IReadOnlyList<string> AllowedStatuses(string kind) => kind switch
    {
        "vsd" => ["INTRODUCED", "RETIRED", "WRITTEN_OFF"],
        "tnved" => ["INTRODUCED"],
        "emissionType" => ["EMITTED", "APPLIED"],
        "partNumber" => ["APPLIED"],
        "productionDate" or "expirationDate" or "variableExpiration" or "weight" or "permitDocs" or "licences"
            => ["APPLIED", "INTRODUCED"],
        _ => []
    };

    public static IReadOnlyList<string> AllowedPackageTypes(string kind) => kind switch
    {
        "productionDate" or "tnved" or "permitDocs" or "emissionType" => ["UNIT", "BUNDLE"],
        "vsd" or "expirationDate" or "variableExpiration" or "weight" or "licences" or "partNumber" => ["UNIT"],
        _ => []
    };

    // Type 5 is restricted to LP_ACCEPT_GOODS, not this document. vetpharma/bio do not allow permit correction.
    public static IReadOnlyList<int> AllowedPermitTypes(string group) => !Supports(group, "permitDocs") ? [] : group switch
    {
        "petfood" or "beer" => [2],
        "nabeer" or "bicycle" or "shoes" or "fire" or "furs" or "electronics" or "tires" => [1, 2],
        _ => [1, 2, 4]
    };

    public static bool AllowsSpecialState(string group, string kind, string? state) => Supports(group, kind) &&
        (string.IsNullOrEmpty(state) || state is "EMPTY" or "WAIT_TRANSFER_TO_OWNER" ||
         (group == "beer" && kind == "permitDocs" && state == "MOVING_BY_UD"));

    public static IReadOnlyList<string> ForbiddenParentPackageTypes(string kind) => kind switch
    {
        "vsd" or "tnved" => ["GROUP"],
        "partNumber" => ["GROUP", "SET"],
        "emissionType" => ["UNIT", "BUNDLE", "GROUP", "SET", "BOX", "ATK"],
        _ => []
    };

    public static bool RequiresNoParent(string kind) => kind == "emissionType";

    public static bool AllowsEmissionType(string kind, string? emissionType) => kind switch
    {
        "tnved" => emissionType is "LOCAL" or "REMAINS" or "CROSSBORDER" or "REMARK" or "COMMISSION" or "REAPPLY",
        "emissionType" => emissionType is "LOCAL" or "FOREIGN",
        _ => Kinds.Any(x => x.Code == kind)
    };

    public static bool AllowsEarlyProductionDate(string group, string? emissionType) =>
        group is "toys" or "radio" or "gadgets" && emissionType == "REAPPLY";

    public static string Describe(string group, string kind)
    {
        if (!Supports(group, kind)) return "Этот вид корректировки недоступен для выбранной товарной группы.";
        var status = kind switch
        {
            "vsd" => "В обороте, Выбыл или Списан", "tnved" => "В обороте",
            "emissionType" => "Эмитирован или Нанесён", "partNumber" => "Нанесён", _ => "Нанесён или В обороте"
        };
        var packaging = AllowedPackageTypes(kind).Count == 2 ? "единица товара или комплект" : "единица товара";
        var extra = kind switch
        {
            "vsd" => " Код не должен входить в групповую упаковку. Товар должен подлежать ветеринарному контролю; новый ВСД должен отличаться от текущего. ЦРПТ проверяет НКМТ, ВетИС и вес/объём продукции по ВСД.",
            "tnved" => " Код не должен входить в групповую упаковку; тип эмиссии не должен быть «Ввезён в РФ».",
            "emissionType" => " Код не должен входить в агрегат; исходный тип эмиссии — «Произведён в РФ» или «Ввезён в РФ».",
            "partNumber" => " Код не должен входить в групповую упаковку или набор. ЦРПТ проверяет наличие партии в реестре, ИНН владельца и код товара.",
            "weight" => " В карточке НКМТ должен быть указан товар с переменным количеством; это условие проверяет ЦРПТ.",
            "variableExpiration" => " В карточке НКМТ должен быть указан вариативный срок годности; индексы условий хранения должны соответствовать карточке. Это проверяет ЦРПТ.",
            "permitDocs" => " ЦРПТ выполняет дополнительные проверки разрешительной документации для соответствующей товарной группы.",
            "licences" => " ЦРПТ проверяет применимость лицензии к источнику воды по карточке НКМТ.",
            "productionDate" when group is "toys" or "radio" or "gadgets" => " Для типа эмиссии REAPPLY дата производства допустима с 01.01.1970; для остальных — за последние 20 лет. Верхняя граница — завтра.",
            _ => ""
        };
        var actor = kind == "emissionType" ? "эмиссию" : kind is "expirationDate" or "variableExpiration" or "weight" or "partNumber"
            ? "нанесение" : "нанесение или ввод в оборот";
        var states = group == "beer" && kind == "permitDocs" ? "; также допускается «Фиксация»" : "";
        return $"Статус: {status}. Тип упаковки: {packaging}. Особое состояние отсутствует или «Ожидает передачу собственнику»{states}." +
            extra + $" Корректировку выполняет участник, осуществивший {actor} кода; право участника проверяет ЦРПТ.";
    }
}
