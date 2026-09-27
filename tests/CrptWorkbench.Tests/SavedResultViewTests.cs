using System.Text.Json;
using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SavedResultViewTests
{
    [Fact]
    public void ExistingResponsesRebuildFriendlyViewsWithoutExchanges()
    {
        var product = new NationalCatalogProductResult("Товар", "04600000000000", "1234", "Обувь", "Опубликована",
            [new NationalCatalogPackage("04600000000017", "Короб", 10)],
            [new NationalCatalogDocument("Декларация", "42", null)], "Часть сведений недоступна");
        var saved = SavedResultView.From(new ApiRequestLog
        {
            OperationId = NationalCatalogProductHandler.OperationId,
            ResponseBody = JsonSerializer.Serialize(product, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ExchangesJson = "not needed for ordinary view"
        });
        Assert.Equal("Товар", saved.Product?.ProductName);
        Assert.Single(saved.Product!.Packages);
        Assert.Single(saved.Product.Documents);
        Assert.Equal("Часть сведений недоступна", saved.Product.Warning);

        var documents = SavedResultView.From(new ApiRequestLog
        {
            OperationId = TrueApiDocumentStatusHandler.OperationId,
            ResponseBody = """[{"number":"1","type":"REAGGREGATION_DOCUMENT","status":"CHECKED_NOT_OK","errors":["Ошибка"]}]"""
        });
        Assert.Single(documents.Documents!.Items);
        Assert.Equal("Ошибка", Assert.Single(DocumentStatusResults.ErrorTexts(documents.Documents.Items[0])));

        var codes = SavedResultView.From(new ApiRequestLog
        {
            OperationId = TrueApiCodeInfoHandler.OperationId,
            ResponseBody = """[{"cis":"010000000000000021TEST","errorCode":"404","errorMessage":"Не найден"}]"""
        });
        Assert.Single(codes.Codes!.Items);
        Assert.Equal(1, codes.Codes.ErrorCount);
    }

    [Fact]
    public void BrokenEmptyAndUnknownResponsesOfferTechnicalView()
    {
        foreach (var body in new[] { null, "", "{invalid", "{}" })
        {
            var result = SavedResultView.From(new ApiRequestLog { OperationId = NationalCatalogProductHandler.OperationId, ResponseBody = body });
            Assert.Null(result.Product);
            Assert.Contains("технический вид", result.Notice!);
        }
        Assert.Contains("технический вид", SavedResultView.From(new ApiRequestLog { OperationId = "other", ResponseBody = "{}" }).Notice!);
    }

    [Fact]
    public void SuzFieldsAndDocumentTypesAreReadable()
    {
        var suz = SavedResultView.From(new ApiRequestLog
        {
            OperationId = "suz.order-status",
            ResponseBody = """{"id":"order-1","status":"Готов","system":"СУЗ","message":"Обработан","mock":true}"""
        });
        Assert.Equal(4, suz.Fields.Count);
        Assert.Equal("Идентификатор заказа", suz.Fields[0].Label);
        Assert.Equal("Трансформация упаковки", DocumentStatusResults.Type("REAGGREGATION_DOCUMENT"));
        Assert.Equal("Формирование набора", DocumentStatusResults.Type("SETS_AGGREGATION"));
        Assert.Equal("Неизвестный тип документа", DocumentStatusResults.Type("SOMETHING_NEW"));
        Assert.Equal("Не указано", DocumentStatusResults.Type(null));
        Assert.DoesNotContain("LP_INTRODUCE_GOODS", DocumentStatusResults.Type("LP_INTRODUCE_GOODS"));
    }
}
