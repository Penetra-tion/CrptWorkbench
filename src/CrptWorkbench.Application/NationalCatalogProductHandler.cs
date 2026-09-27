using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Application;

public sealed record NationalCatalogProductRequest(string Gtin);
public sealed record NationalCatalogPackage(string Gtin, string Level, int Multiplier);
public sealed record NationalCatalogDocument(string Type, string Number, string? Date);
public sealed record NationalCatalogProductResult(
    string ProductName,
    string Gtin,
    string TnVed,
    string ProductGroup,
    string CardStatus,
    IReadOnlyList<NationalCatalogPackage> Packages,
    IReadOnlyList<NationalCatalogDocument> Documents,
    string? Warning = null);

public sealed class NationalCatalogProductHandler(INationalCatalogClient client) : IApiOperationHandler
{
    public const string OperationId = "national-catalog.product";
    private static readonly Regex GtinPattern = new("^(?:[0-9]{8}|[0-9]{12}|[0-9]{13}|[0-9]{14})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyDictionary<int, string> ProductGroups = new Dictionary<int, string>
    {
        [1] = "Лёгкая промышленность", [2] = "Обувные товары", [3] = "Табачная продукция",
        [4] = "Духи и туалетная вода", [5] = "Шины и покрышки", [6] = "Фототовары",
        [8] = "Молочная продукция", [9] = "Велосипеды и рамы", [10] = "Медицинские изделия",
        [11] = "Алкоголь", [12] = "Альтернативная табачная продукция", [13] = "Упакованная вода",
        [14] = "Товары из натурального меха", [15] = "Пиво и слабоалкогольные напитки",
        [16] = "Никотиносодержащая продукция", [17] = "Специализированная пищевая продукция и БАД к пище",
        [19] = "Антисептики и дезинфицирующие средства", [20] = "Корма для животных", [21] = "Морепродукты",
        [22] = "Безалкогольное пиво", [23] = "Соковая продукция и безалкогольные напитки", [25] = "Мясные изделия",
        [26] = "Ветеринарные препараты", [27] = "Игры и игрушки для детей", [28] = "Радиоэлектронная продукция",
        [31] = "Титановая металлопродукция", [32] = "Консервированная продукция", [33] = "Растительные масла",
        [34] = "Оптоволокно и оптоволоконная продукция", [35] = "Косметика, бытовая химия и товары личной гигиены",
        [36] = "Печатная продукция", [37] = "Бакалейная продукция", [38] = "Фармацевтическое сырьё и лекарства",
        [39] = "Строительные материалы", [40] = "Пожарная безопасность", [41] = "Отопительные приборы",
        [42] = "Кабельно-проводниковая продукция", [43] = "Моторные масла", [44] = "Полимерные трубы",
        [45] = "Сладости и кондитерские изделия", [48] = "Автозапчасти", [49] = "Натуральный мех",
        [50] = "Радиоэлектронная продукция. Электронные системы доставки никотина",
        [51] = "Радиоэлектронная продукция. Ноутбуки и смартфоны", [52] = "Полуфабрикаты и замороженные продукты",
        [53] = "Удобрения в потребительской упаковке", [54] = "Товары для дома и интерьера", [59] = "Пиротехнические изделия"
    };

    public ApiOperationDescriptor Descriptor { get; } = new(
        OperationId,
        "Запрос по GTIN",
        ApiSystem.NationalCatalog,
        "GET",
        "Поиск собственной карточки товара, упаковок и разрешительных документов.",
        typeof(NationalCatalogProductRequest),
        typeof(NationalCatalogProductResult),
        [new("gtin", "GTIN", "04600000000000", HelpText: "8, 12, 13 или 14 цифр")]);

    public async Task<IntegrationExchange> ExecuteAsync(
        IReadOnlyDictionary<string, string> values,
        ApiOperationContext context,
        CancellationToken cancellationToken)
    {
        var gtin = values.GetValueOrDefault("gtin")?.Trim();
        if (gtin is null || !GtinPattern.IsMatch(gtin))
            throw new InvalidOperationException("GTIN должен содержать 8, 12, 13 или 14 цифр.");

        var headers = new Dictionary<string, string> { ["X-Correlation-Id"] = context.CorrelationId };
        var authentication = new IntegrationAuthenticationContext(context.Profile.Id, context.Profile.Inn, context.Profile.CertificateThumbprint);

        var cardRequest = new IntegrationRequest(
            OperationId,
            "GET",
            $"{client.BaseAddress.AbsoluteUri.TrimEnd('/')}/v3/feed-product?gtin={Uri.EscapeDataString(gtin)}&format=json",
            headers,
            null,
            context.CorrelationId,
            authentication);
        var cardExchange = await client.ExecuteAsync(cardRequest, cancellationToken);
        if (cardExchange.StatusCode is < 200 or >= 300)
            return cardExchange with { Exchanges = [cardExchange] };

        var card = ParseCard(cardExchange.ResponseBody, gtin);
        var exchanges = new List<IntegrationExchange> { cardExchange };
        var documents = card.Documents;
        string? warning = null;

        if (string.Equals(card.RawStatus, "published", StringComparison.OrdinalIgnoreCase))
        {
            object payload = string.IsNullOrWhiteSpace(card.OwnerInn) ? new { gtin } : new { gtin, inn = card.OwnerInn };
            var body = JsonSerializer.Serialize(payload, JsonOptions);
            var documentRequest = new IntegrationRequest(
                OperationId,
                "POST",
                $"{client.BaseAddress.AbsoluteUri.TrimEnd('/')}/v4/rd-info-by-gtin",
                new Dictionary<string, string>(headers) { ["Content-Type"] = "application/json; charset=utf-8" },
                body,
                context.CorrelationId,
                authentication);
            var documentExchange = await client.ExecuteAsync(documentRequest, cancellationToken);
            exchanges.Add(documentExchange);
            if (documentExchange.StatusCode is >= 200 and < 300)
                documents = ParseDocuments(documentExchange.ResponseBody);
            else
                warning = $"Карточка получена, но разрешительные документы недоступны (HTTP {documentExchange.StatusCode}).";
        }

        var result = new NationalCatalogProductResult(
            card.ProductName,
            gtin,
            card.TnVed,
            card.ProductGroup,
            TranslateStatus(card.RawStatus),
            card.Packages,
            documents,
            warning);

        return cardExchange with
        {
            ResponseBody = JsonSerializer.Serialize(result, JsonOptions),
            Duration = TimeSpan.FromTicks(exchanges.Sum(exchange => exchange.Duration.Ticks)),
            Exchanges = exchanges
        };
    }

    private static ParsedCard ParseCard(string? body, string requestedGtin)
    {
        using var document = JsonDocument.Parse(body ?? "{}");
        var root = document.RootElement;
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            throw new InvalidOperationException("Национальный каталог вернул ответ без карточки товара.");
        var item = result[0];
        var attributes = item.TryGetProperty("good_attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Array ? attrs : default;
        var packages = new List<NationalCatalogPackage>();
        if (item.TryGetProperty("identified_by", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var identifier in identifiers.EnumerateArray())
            {
                if (!string.Equals(GetString(identifier, "type"), "gtin", StringComparison.OrdinalIgnoreCase)) continue;
                packages.Add(new(
                    GetString(identifier, "value") ?? "Не указано",
                    TranslateLevel(GetString(identifier, "level")),
                    GetInt(identifier, "multiplier") ?? 1));
            }
        }

        var tnVed = AttributeValue(attributes, 13933) ?? AttributeValue(attributes, 3959) ?? "Не указано";
        var groupValue = AttributeValue(attributes, 23768);
        // The catalog also returns the card category outside good_attrs.
        if (string.IsNullOrWhiteSpace(groupValue) &&
            item.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            groupValue = categories.EnumerateArray()
                .Select(category => GetString(category, "cat_name"))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        }
        var group = int.TryParse(groupValue, out var groupId) && ProductGroups.TryGetValue(groupId, out var groupName)
            ? groupName
            : groupValue ?? "Не указано";
        var rawDocuments = ParseAttributeDocuments(attributes);

        return new(
            GetString(item, "good_name") ?? "Не указано",
            GetString(item, "good_status") ?? "unknown",
            GetString(item, "producer_inn"),
            tnVed,
            group,
            packages.Count == 0 ? [new(requestedGtin, "Потребительская", 1)] : packages,
            rawDocuments);
    }

    private static IReadOnlyList<NationalCatalogDocument> ParseDocuments(string? body)
    {
        using var document = JsonDocument.Parse(body ?? "{}");
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array)
            return [];
        return documents.EnumerateArray().Select(item => new NationalCatalogDocument(
            DocumentType(GetInt(item, "attr_id")),
            GetString(item, "number") ?? "Не указано",
            GetString(item, "from_date"))).ToArray();
    }

    private static IReadOnlyList<NationalCatalogDocument> ParseAttributeDocuments(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Array) return [];
        return attributes.EnumerateArray()
            .Select(attribute => new { Attribute = attribute, Id = GetInt(attribute, "attr_id") })
            .Where(item => item.Id is 23561 or 23557 or 23765)
            .Select(item => new NationalCatalogDocument(
                DocumentType(item.Id),
                GetString(item.Attribute, "attr_value") ?? item.Attribute.GetRawText(),
                null))
            .ToArray();
    }

    private static string? AttributeValue(JsonElement attributes, int id)
    {
        if (attributes.ValueKind != JsonValueKind.Array) return null;
        foreach (var attribute in attributes.EnumerateArray())
            if (GetInt(attribute, "attr_id") == id) return GetString(attribute, "attr_value");
        return null;
    }

    private static string TranslateStatus(string status) => status.ToLowerInvariant() switch
    {
        "draft" => "Черновик", "moderation" => "На модерации", "errors" => "Требует изменений",
        "notsigned" => "Ожидает подписания", "published" => "Опубликована", "archived" => "В архиве",
        _ => status
    };

    private static string TranslateLevel(string? level) => level switch
    {
        "trade-unit" => "Потребительская", "inner-pack" => "Групповая потребительская", "box" => "Транспортная",
        "layer" => "Слой на паллете", "pallet" => "Паллета", "metro-unit" => "Метро-юнит", "show-pack" => "Шоу-пак",
        _ => string.IsNullOrWhiteSpace(level) ? "Не указано" : level
    };

    private static string DocumentType(int? id) => id switch
    {
        23561 => "Сертификат соответствия", 23557 => "Декларация о соответствии",
        23765 => "Свидетельство о государственной регистрации", _ => id?.ToString() ?? "Не указано"
    };

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static int? GetInt(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)) return number;
        return int.TryParse(property.ToString(), out number) ? number : null;
    }

    private sealed record ParsedCard(
        string ProductName,
        string RawStatus,
        string? OwnerInn,
        string TnVed,
        string ProductGroup,
        IReadOnlyList<NationalCatalogPackage> Packages,
        IReadOnlyList<NationalCatalogDocument> Documents);
}
