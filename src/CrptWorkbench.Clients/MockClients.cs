using System.Diagnostics;
using System.Text.Json;
using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

internal static class MockExchangeFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<IntegrationExchange> ExecuteAsync(ApiSystem system, IntegrationRequest request, CancellationToken cancellationToken,
        string? responseBody = null, int statusCode = 200)
    {
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(system switch
        {
            ApiSystem.TrueApi => 85,
            ApiSystem.NationalCatalog => 110,
            _ => 70
        }, cancellationToken);
        stopwatch.Stop();

        var value = ReadQueryValue(request.Url, "value") ?? "—";

        var response = responseBody ?? JsonSerializer.Serialize(new
        {
            mock = true,
            id = value,
            status = "Найдено",
            system = SystemName(system),
            correlationId = request.CorrelationId,
            message = "Ответ сформирован локальным mock-клиентом. Сетевой запрос не выполнялся."
        }, JsonOptions);

        var requestHeaders = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer mock-token-not-a-real-secret"
        };

        return new IntegrationExchange(
            statusCode,
            request.Method,
            request.Url,
            requestHeaders,
            request.Body,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json; charset=utf-8",
                ["X-Correlation-Id"] = request.CorrelationId,
                ["X-CRPT-Workbench-Mock"] = "true"
            },
            response,
            stopwatch.Elapsed);
    }

    private static string? ReadQueryValue(string url, string name)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
            }
        }

        return null;
    }

    private static string SystemName(ApiSystem system) => system switch
    {
        ApiSystem.TrueApi => "True API",
        ApiSystem.NationalCatalog => "Национальный каталог",
        _ => "СУЗ"
    };
}

public sealed class MockTrueApiClient : ITrueApiClient
{
    public Uri BaseAddress { get; } = new("https://production.trueapi.mock.invalid");

    private const string UnitOne = "010460000000000021DEMO000000001";
    private const string UnitTwo = "010460000000000021DEMO000000002";
    private const string Group = "046000000000000001";
    private const string Box = "000000000000000001";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PackageCreationRequest.IsCreation(request.OperationId))
        {
            var creation = PackageCreationRequest.Validate(request);
            using var document = JsonDocument.Parse(creation.Document);
            var correction = creation.Type == "CIS_INFORMATION_CHANGE";
            var package = correction ? null : creation.Type is "AGGREGATION_DOCUMENT" or "SETS_AGGREGATION"
                ? document.RootElement.GetProperty("aggregationUnits")[0].GetProperty("unitSerialNumber").GetString()
                : document.RootElement.GetProperty("uitu").GetString();
            // Fixed marker + type/group/error metadata survive client and process recreation.
            // An 18-digit package code of all nines is the documented mock rejection example.
            var groupMarker = correction ? CodeCorrectionRules.Groups.Single(x => x.Code == creation.Group).Id : Array.IndexOf(PackageCreationRequest.Groups, creation.Group);
            var marker = $"c0de{(correction ? 4 : creation.Type == "SETS_AGGREGATION" ? 3 : creation.Type == "AGGREGATION_DOCUMENT" ? 1 : 2):x1}{groupMarker:x2}{(package == "999999999999999999" ? 1 : 0):x1}";
            var id = marker + Guid.NewGuid().ToString()[8..];
            return MockExchangeFactory.ExecuteAsync(ApiSystem.TrueApi, request with { Url = $"https://production.trueapi.mock.invalid/api/v3/true-api/{request.Url}" },
                cancellationToken, JsonSerializer.Serialize(id, JsonOptions), 201);
        }
        if (request.OperationId == "true-api.document-status")
        {
            var url = DocumentInfoRequestUri.Build(request.Url, "production.trueapi.mock.invalid").AbsoluteUri;
            var id = Uri.UnescapeDataString(request.Url[4..request.Url.IndexOf("/info?", StringComparison.Ordinal)]);
            var documents = id switch
            {
                "demo-code-emission" => new object[] { new { number = id, receivedAt = "2026-09-17T14:33:00Z", type = "EMISSION", senderName = "Демонстрационный производитель", senderInn = "0000000000", status = "ACCEPTED" } },
                "demo-code-payment" => new object[] { new { number = id, receivedAt = "2026-09-24T03:54:00Z", type = "PAYMENT", senderName = "Демонстрационный производитель", senderInn = "0000000000", status = "ACCEPTED" } },
                "demo-code-application" => new object[] { new { number = id, receivedAt = "2026-09-24T03:54:00Z", type = "APPLIED", senderName = "Демонстрационный производитель", senderInn = "0000000000", status = "ACCEPTED" } },
                "demo-code-aggregation" => new object[] { new { number = id, receivedAt = "2026-09-24T04:07:00Z", type = "AGGREGATION_DOCUMENT", senderName = "Демонстрационный производитель", senderInn = "0000000000", status = "ACCEPTED" } },
                "demo-code-introduction" => new object[] { new { number = id, receivedAt = "2026-09-24T04:28:00Z", type = "LP_INTRODUCE_GOODS", senderName = "Демонстрационный производитель", senderInn = "0000000000", status = "ACCEPTED" } },
                "demo-success" => new object[] { new { number = id, receivedAt = "2026-09-23T09:15:00Z", type = "LP_INTRODUCE_GOODS", productGroup = new[] { "lp", "shoes" }, status = "ACCEPTED", errors = Array.Empty<string>(), commonErrors = Array.Empty<object>() } },
                "demo-pending" => new object[] { new { number = id, receivedAt = "2026-09-23T09:16:00Z", type = "LP_INTRODUCE_GOODS", productGroup = new[] { "lp" }, status = "IN_PROGRESS", errors = Array.Empty<string>(), commonErrors = Array.Empty<object>() } },
                "demo-error" => new object[] { new { number = id, receivedAt = "2026-09-23T09:17:00Z", type = "LP_INTRODUCE_GOODS", productGroup = new[] { "lp" }, status = "PARSE_ERROR", errors = new[] { "Некорректный формат документа" }, commonErrors = new object[] { new { errorMessage = "Некорректный формат документа" }, new { errorMessage = "Отсутствует обязательное поле" } } } },
                _ when Guid.TryParse(id, out _) => new[] { CreatePackageDocumentStatus(id) },
                _ => []
            };
            return MockExchangeFactory.ExecuteAsync(ApiSystem.TrueApi, request with { Url = url }, cancellationToken,
                JsonSerializer.Serialize(documents, JsonOptions));
        }
        if (request.OperationId == "true-api.code-info" && request.Url.Contains("/cises/history?cis=", StringComparison.Ordinal))
        {
            var code = Uri.UnescapeDataString(request.Url[(request.Url.IndexOf("?cis=", StringComparison.Ordinal) + 5)..]);
            var ids = code == UnitOne
                ? new[] { "demo-code-emission", "demo-code-payment", "demo-code-application", "demo-code-aggregation", "demo-code-introduction" }
                : code == UnitTwo ? new[] { "demo-code-emission", "demo-code-application" } : [];
            var events = ids.Select((id, index) => new { cis = code, docId = id, timestamp = new DateTimeOffset(2026, 9, 24, 3, 54, 0, TimeSpan.Zero).AddMinutes(index) }).ToArray();
            return MockExchangeFactory.ExecuteAsync(ApiSystem.TrueApi, request, cancellationToken,
                JsonSerializer.Serialize(events, JsonOptions), ids.Length == 0 ? 404 : 200);
        }
        if (request.OperationId != "true-api.code-info")
            return MockExchangeFactory.ExecuteAsync(ApiSystem.TrueApi, request, cancellationToken);

        var codes = JsonSerializer.Deserialize<string[]>(request.Body ?? "[]", JsonOptions) ?? [];
        var items = codes.Select(CreateItem).ToArray();
        return MockExchangeFactory.ExecuteAsync(ApiSystem.TrueApi, request, cancellationToken,
            JsonSerializer.Serialize(items, JsonOptions), items.Any(x => x.ErrorCode is null) ? 200 : 404);
    }

    private static object CreatePackageDocumentStatus(string id)
    {
        var correctionGroup = id.StartsWith("c0de4", StringComparison.Ordinal) &&
            int.TryParse(id.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var correctionId)
            ? CodeCorrectionRules.Groups.FirstOrDefault(x => x.Id == correctionId)?.Code : null;
        var marked = id.StartsWith("c0de", StringComparison.Ordinal) && id[4] is '1' or '2' or '3' &&
            int.TryParse(id.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var index) && index < PackageCreationRequest.Groups.Length;
        var groupIndex = marked ? Convert.ToInt32(id.Substring(5, 2), 16) : Array.IndexOf(PackageCreationRequest.Groups, "milk");
        var error = marked && id[7] == '1';
        return new
        {
            number = id, receivedAt = "2026-09-27T00:00:00Z",
            type = correctionGroup is not null ? "CIS_INFORMATION_CHANGE" : marked && id[4] == '3' ? "SETS_AGGREGATION" : marked && id[4] == '1' ? "AGGREGATION_DOCUMENT" : "REAGGREGATION_DOCUMENT",
            productGroup = new[] { correctionGroup ?? PackageCreationRequest.Groups[groupIndex] },
            status = error ? "CHECKED_NOT_OK" : "CHECKED_OK",
            errors = error ? new[] { "Демонстрационная ошибка: упаковка 999999999999999999 отклонена." } : Array.Empty<string>(),
            commonErrors = Array.Empty<object>()
        };
    }

    private static CodeInfoItem CreateItem(string code)
    {
        if (code is "010460000000000021CORRECT000001" or "010460000000000021CORRECT000002" or
            "010460000000000021CORRECT000003" or "010460000000000021CORRECT000004")
            return new(new CisInfo
            {
                RequestedCis = code, Cis = code, Gtin = "04600000000000",
                ProductName = "Демо: код для корректировки", ProductGroup = code.EndsWith("3", StringComparison.Ordinal) ? "shoes" : "milk",
                ProductGroupId = code.EndsWith("3", StringComparison.Ordinal) ? 2 : 8,
                GeneralPackageType = "UNIT", PackageType = "UNIT", StatusEx = "EMPTY", EmissionType = "LOCAL",
                Status = code.EndsWith("2", StringComparison.Ordinal) ? "RETIRED" : code.EndsWith("1", StringComparison.Ordinal) ? "INTRODUCED" : "APPLIED",
                OwnerInn = "0000000000", ProducerInn = "0000000000"
            });
        if (code == Box)
            return new(new CisInfo
            {
                RequestedCis = code,
                Cis = code,
                ProductGroup = "milk",
                ProductGroupId = 8,
                GeneralPackageType = "BOX",
                Status = "INTRODUCED",
                StatusEx = "EMPTY",
                OwnerName = "Демонстрационный владелец",
                OwnerInn = "0000000000",
                EmissionType = "LOCAL",
                EmissionDate = new DateTimeOffset(2025, 5, 30, 8, 0, 0, TimeSpan.Zero),
                Child = [Group]
            });

        if (code == Group)
            return new(new CisInfo
            {
                RequestedCis = code,
                Cis = code,
                Gtin = "04600000000000",
                ProductName = "Демо: групповая упаковка молока",
                ProductGroup = "milk",
                ProductGroupId = 8,
                Brand = "Демонстрационная марка",
                GeneralPackageType = "GROUP",
                Status = "INTRODUCED",
                StatusEx = "EMPTY",
                OwnerName = "Демонстрационный владелец",
                OwnerInn = "0000000000",
                ProducerName = "Демонстрационный производитель",
                ProducerInn = "0000000000",
                EmissionType = "LOCAL",
                EmissionDate = new DateTimeOffset(2025, 5, 31, 9, 0, 0, TimeSpan.Zero),
                ApplicationDate = new DateTimeOffset(2025, 6, 2, 10, 0, 0, TimeSpan.Zero),
                ExpirationDate = new DateTimeOffset(2025, 6, 12, 0, 0, 0, TimeSpan.Zero),
                Child = [UnitOne, UnitTwo],
                Parent = Box
            });

        if (code is not (UnitOne or UnitTwo))
            return new(new CisInfo { RequestedCis = code, Cis = code }, "404", "КИ не найден");

        var retired = code == UnitTwo;
        return new(new CisInfo
        {
            RequestedCis = code, Cis = code, Gtin = "04600000000000",
            ProductName = retired ? "Демо: молоко 2,5%" : "Демо: молоко 3,2%",
            ProductGroup = "milk", ProductGroupId = 8, Brand = "Демонстрационная марка",
            Status = retired ? "RETIRED" : "INTRODUCED", StatusEx = "EMPTY",
            OwnerName = "Демонстрационный владелец", OwnerInn = "0000000000",
            ProducerName = "Демонстрационный производитель", ProducerInn = "0000000000",
            GeneralPackageType = "UNIT", PackageType = "UNIT", EmissionType = "LOCAL",
            EmissionDate = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.Zero),
            ProducedDate = new DateTimeOffset(2025, 6, 2, 9, 0, 0, TimeSpan.Zero),
            ApplicationDate = new DateTimeOffset(2025, 6, 2, 10, 30, 0, TimeSpan.Zero),
            IntroducedDate = new DateTimeOffset(2025, 6, 2, 12, 0, 0, TimeSpan.Zero),
            ExpirationDate = retired ? new DateTimeOffset(2025, 6, 12, 0, 0, 0, TimeSpan.Zero) : null,
            Expirations = retired ? null :
            [
                new()
                {
                    ExpirationStorageDate = new DateTimeOffset(2025, 6, 12, 0, 0, 0, TimeSpan.Zero),
                    StorageConditionId = 1,
                    StorageConditionName = "10 суток при температуре от 2°C до 6°C"
                },
                new()
                {
                    ExpirationStorageDate = new DateTimeOffset(2025, 6, 7, 0, 0, 0, TimeSpan.Zero),
                    StorageConditionId = 2,
                    StorageConditionName = "5 суток после вскрытия упаковки"
                }
            ],
            CertDoc = retired ? null :
            [
                new()
                {
                    Type = "CONFORMITY_DECLARATION",
                    Number = "RU Д-DEMO.АА00.B.00001/25",
                    Date = "2025.05.20",
                    Indx = 123,
                    StatusGroup = 1
                }
            ],
            CertDocRequirementDetails = retired
                ? new() { NotRequiredReason = "Для демонстрационного товара документ не требуется", IsRequired = true }
                : null,
            Parent = Group
        });
    }
}

public sealed class MockNationalCatalogClient : INationalCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public Uri BaseAddress { get; } = new("https://production.nationalcatalog.mock.invalid");

    public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(request.Url);
        if (uri.AbsolutePath == "/v3/feed-product")
        {
            var gtin = ReadQuery(uri, "gtin") ?? "04600000000000";
            var response = JsonSerializer.Serialize(new
            {
                apiversion = 3,
                result = new[]
                {
                    new
                    {
                        good_name = "Демонстрационный товар Национального каталога",
                        good_status = "published",
                        producer_inn = "7700000000",
                        identified_by = new object[]
                        {
                            new { value = gtin, type = "gtin", multiplier = 1, level = "trade-unit" },
                            new { value = "14600000000007", type = "gtin", multiplier = 12, level = "inner-pack" },
                            new { value = "24600000000004", type = "gtin", multiplier = 48, level = "box" }
                        },
                        good_attrs = new object[]
                        {
                            new { attr_id = 13933, attr_name = "Код ТНВЭД", attr_value = "1905319900" },
                            new { attr_id = 23768, attr_name = "Товарная группа", attr_value = "45" }
                        }
                    }
                }
            }, JsonOptions);
            return MockExchangeFactory.ExecuteAsync(ApiSystem.NationalCatalog, request, cancellationToken, response);
        }

        if (uri.AbsolutePath == "/v4/rd-info-by-gtin")
        {
            var response = JsonSerializer.Serialize(new
            {
                apiversion = 4,
                result = new
                {
                    documents = new object[]
                    {
                        new { attr_id = 23557, number = "ЕАЭС N RU Д-DEMO.АА00.В.00001/25", from_date = "2025-05-20" },
                        new { attr_id = 23561, number = "ЕАЭС RU С-DEMO.АА00.В.00002/25", from_date = "2025-06-01" }
                    }
                }
            }, JsonOptions);
            return MockExchangeFactory.ExecuteAsync(ApiSystem.NationalCatalog, request, cancellationToken, response);
        }

        throw new InvalidOperationException("Mock National Catalog endpoint is not supported.");
    }

    private static string? ReadQuery(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }
}

public sealed class MockSuzClient : ISuzClient
{
    public Task<IntegrationExchange> ExecuteAsync(IntegrationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OperationId != "suz.document-status")
            return MockExchangeFactory.ExecuteAsync(ApiSystem.Suz, request, cancellationToken);
        var uri = new Uri(new Uri("https://production.suz.mock.invalid/"), request.Url);
        var id = uri.Query.TrimStart('?').Split('&').FirstOrDefault(x => x.StartsWith("resultDocId=", StringComparison.Ordinal))?[12..];
        if (request.Method != "GET" || request.Body is not null || uri.AbsolutePath != "/api/v3/receipts/receipt" ||
            !Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("Некорректный запрос квитанции СУЗ.");
        var state = id switch
        {
            "00000000-0000-0000-0000-000000000002" => "FAILED",
            "00000000-0000-0000-0000-000000000003" => "IN_PROGRESS",
            "00000000-0000-0000-0000-000000000004" => "PARTIALLY",
            _ => "SUCCESS"
        };
        var results = new List<object>();
        if (id != "00000000-0000-0000-0000-000000000006") results.Add(Receipt(id!, state, "REPORT_UTILIZE", 1));
        if (id == "00000000-0000-0000-0000-000000000005") results.Add(Receipt(id, "IN_PROGRESS", "CREATE_ORDER", 2));
        var headers = new Dictionary<string, string>(request.Headers) { ["clientToken"] = "mock-suz-token-not-a-secret" };
        return MockExchangeFactory.ExecuteAsync(ApiSystem.Suz, request with
        {
            Url = "https://production.suz.mock.invalid" + uri.PathAndQuery,
            Headers = headers
        }, cancellationToken, JsonSerializer.Serialize(new { results }));
    }

    private static object Receipt(string id, string state, string workflow, int number) => new
    {
        resultDocId = $"10000000-0000-0000-0000-{number:D12}",
        resultDocDate = 1790500200000L,
        sourceDocId = id,
        sourceDocDate = 1790500000000L,
        state,
        code = state switch { "FAILED" => 1, "IN_PROGRESS" => 2, "PARTIALLY" => 3, _ => 0 },
        description = state switch
        {
            "FAILED" => "Документ отклонён: код не принадлежит участнику.",
            "IN_PROGRESS" => "Документ обрабатывается.",
            "PARTIALLY" => "Часть кодов обработана с ошибками.",
            _ => "Документ успешно обработан."
        },
        workflow,
        workflowVersion = 1,
        details = new { productGroupIds = new[] { 3 }, txType = workflow, quantity = 2 },
        operations = new[]
        {
            new
            {
                operationId = "20000000-0000-0000-0000-000000000001",
                operationType = workflow + "_RECEIVED", operationDate = 1790500000000L,
                docId = id, details = new { state = "SUCCESS", description = "Документ получен.", errorMessage = (string?)null }
            },
            new
            {
                operationId = "20000000-0000-0000-0000-000000000002",
                operationType = "RNMS_GIS_PROCESSED", operationDate = 1790500200000L,
                docId = id, details = new { state, description = "Проверка кодов.",
                    errorMessage = state is "FAILED" or "PARTIALLY" ? "Код не принадлежит участнику." : null }
            }
        }
    };
}
