using System.Text.Json;
using CrptWorkbench.Application;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzUtilisationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(3));
    private static string MilkCode(int serial = 1, string gtin = "04640016748389") => "01" + gtin + "21" + serial.ToString("D6") + "\u001d93dGVz";
    private static SuzUtilisationRequest Milk(params SuzUtilisationRow[] rows) => new("milk", "UNIT", "UTILISATION",
        new Dictionary<string, string> { ["productionDate"] = "2026-10-01", ["expDate"] = "2026-10-10" }, rows);
    private static SuzUtilisationRow Row(string code) => new(code, new Dictionary<string, string>());

    [Fact]
    public void MatrixCoversAllCommonSuzGroupsAndAutomaticReports()
    {
        Assert.Equal(47, SuzUtilisationRules.Groups.Count);
        Assert.Equal(42, SuzUtilisationRules.Groups.Count(x => !x.Automatic));
        Assert.Equal(47, SuzUtilisationRules.Groups.Select(x => x.Id).Distinct().Count());
        Assert.DoesNotContain(SuzUtilisationRules.Groups, x => x.Code is "pharma" or "furs");
        Assert.Equal(new[] { "electronics", "lp", "perfumery", "shoes", "tires" }, SuzUtilisationRules.Groups.Where(x => x.Automatic).Select(x => x.Code).Order().ToArray());
        foreach (var group in SuzUtilisationRules.Groups.Where(x => x.Automatic))
        { Assert.Empty(SuzUtilisationRules.PackageTypes(group.Code)); Assert.Empty(SuzUtilisationRules.Scenarios(group.Code)); }
        Assert.Equal(new[] { "UNIT", "SET" }, SuzUtilisationRules.PackageTypes("furslp").Select(x => x.Code));
        Assert.Equal(new[] { "UNIT" }, SuzUtilisationRules.PackageTypes("titan").Select(x => x.Code));
        Assert.Equal(new[] { "UNIT", "GROUP", "SET" }, SuzUtilisationRules.PackageTypes("wheelchairs").Select(x => x.Code));
        Assert.DoesNotContain(SuzUtilisationRules.PackageTypes("vetbio"), x => x.Code == "SET");
    }

    [Fact]
    public void EveryDocumentedManualGroupAndPackageProducesAProtocolDraft()
    {
        foreach (var group in SuzUtilisationRules.Groups.Where(x => !x.Automatic)) foreach (var package in SuzUtilisationRules.PackageTypes(group.Code))
        {
            var values = new Dictionary<string, string>();
            foreach (var field in SuzUtilisationRules.Fields(group.Code, package.Code, "UTILISATION"))
            {
                if (field.Code.StartsWith('_')) values[field.Code] = field.Code == "_participantKind" ? "LEGAL" : "0";
                else if (field.Code == "kpp") values[field.Code] = "123456789";
                else if (field.Code == "participantId") values[field.Code] = "1234567890";
                else if (field.Code == "productionDate") values[field.Code] = group.Code is "tobacco" or "otp" or "ncp" ? "2026-10-03T10:00:00+0300" : "2026-10-03";
                else if (field.Code == "expDate") values[field.Code] = "2026-10-10";
                else if (field.Code == "expirationDate") values[field.Code] = "2026-10-10";
                else if (field.Code == "seriesNumber") values[field.Code] = "SN-1";
                else if (field.Required) values[field.Code] = field.Code switch { "documentDate" => "2026-10-03", "alcoholVolume" => "0", _ => "Document-1" };
            }
            var shortLength = group.Code switch
            {
                "milk" or "seafood" or "petfood" or "chemistry" or "conserve" or "heater" or "polymer" or "sweets" or "carparts" or "meat" or "vetbio" or "homeware" or "industrial" or "fertilizers" or "frozen" or "pyrotechnics" or "toys" or "fire" => 6,
                "tobacco" or "otp" or "ncp" or "nabeer" or "beer" => 7,
                "radio" or "gadgets" => 20,
                _ => 13
            };
            var code = "010464001674838921" + new string('A', shortLength) + "\u001d93dGVz";
            if (group.Code is "bicycle" or "wheelchairs" or "vetpharma" or "books" or "furslp") code = "010464001674838921" + new string('A', 13) + "\u001d91ABCD\u001d92" + new string('A', 44);
            if (group.Code is "tobacco" or "otp" or "ncp") code = package.Code == "UNIT" ? "04640016748389ABCDEFG1234dGVz" : "010464001674838921ABCDEFG\u001d8005000000\u001d93dGVz";
            var result = SuzUtilisationInput.Prepare(new(group.Code, package.Code, "UTILISATION", values, [Row(code)]), Now);
            Assert.True(result.IsValid, $"{group.Code}/{package.Code}: {string.Join(";", result.Errors.Select(x => x.Field + ":" + x.Message))}");
        }
    }

    [Fact]
    public void GroupMilkForbidsDatesAndIndividualWeight()
    {
        Assert.DoesNotContain(SuzUtilisationRules.Fields("milk", "GROUP", "UTILISATION"), x => x.Code is "productionDate" or "expDate" or "expDateConditions" or "batchNumber");
        Assert.Empty(SuzUtilisationRules.IndividualFields("milk", "GROUP"));
        var valid = SuzUtilisationInput.Prepare(new("milk", "GROUP", "UTILISATION", new Dictionary<string, string>(), [Row(MilkCode())]), Now);
        Assert.True(valid.IsValid);
        var invalid = SuzUtilisationInput.Prepare(Milk(Row(MilkCode())) with { PackageType = "GROUP" }, Now);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, x => x.Field == "productionDate");
    }

    [Theory]
    [InlineData(29999, 1)]
    [InlineData(30000, 1)]
    [InlineData(30001, 2)]
    public void ReportBoundariesPreserveEveryFullCode(int count, int reportCount)
    {
        var result = SuzUtilisationInput.Prepare(Milk(Enumerable.Range(1, count).Select(x => Row(MilkCode(x))).ToArray()), Now);
        Assert.True(result.IsValid, string.Join(";", result.Errors.Select(x => x.Message)));
        Assert.Equal(count, result.CodeCount);
        Assert.Equal(reportCount, result.Reports.Count);
        Assert.Equal(count, result.Reports.Sum(x => x.CodeCount));
        using var json = JsonDocument.Parse(result.Reports[0].Json);
        Assert.Equal(MilkCode(), json.RootElement.GetProperty("sntins")[0].GetString());
        Assert.False(json.RootElement.TryGetProperty("packageType", out _));
    }

    [Fact]
    public void SingleColumnCsvPreservesFullCodeAndRejectsTsv()
    {
        var fullCode = MilkCode() + "\u001d3103001324";
        var rows = SuzUtilisationInput.ParseCsv("\ufeffcode;ignored\n\"" + fullCode.Replace("\u001d", "\\u001D") + "\";anything\n");
        var result = SuzUtilisationInput.Prepare(Milk(rows.ToArray()), Now);
        Assert.True(result.IsValid);
        using var json = JsonDocument.Parse(result.Reports.Single().Json);
        Assert.Equal(fullCode, json.RootElement.GetProperty("sntins")[0].GetString());
        Assert.Equal("code\n", SuzUtilisationInput.TemplateCsv());
        Assert.Throws<FormatException>(() => SuzUtilisationInput.ParseText("code\t3103\n" + fullCode));
        var same = SuzUtilisationInput.Prepare(Milk(new SuzUtilisationRow(MilkCode() + "\u001d3103001324", new Dictionary<string, string> { ["3103"] = "001324" })), Now);
        Assert.True(same.IsValid);
        Assert.Equal(result.Reports.Single().Json, same.Reports.Single().Json);
    }

    [Fact]
    public void CsvHandlesQuotedDelimitersEscapedQuotesAndBlankRows()
    {
        var rows = SuzUtilisationInput.ParseCsv("code,other\n\"CODE;WITH,COMMA\"\"QUOTE\",ignored\n\n");
        Assert.Single(rows);
        Assert.Equal("CODE;WITH,COMMA\"QUOTE", rows[0].Code);
        Assert.Throws<FormatException>(() => SuzUtilisationInput.ParseCsv("code,other\n\"unclosed"));
        Assert.Throws<FormatException>(() => SuzUtilisationInput.ParseCsv("code\tother\nvalue"));
    }

    [Fact]
    public void PackageDatesAreAbsentAndProductionDefaultsToYes()
    {
        foreach (var package in new[] { "GROUP", "SET" })
        {
            var fields = SuzUtilisationRules.Fields("milk", package, "UTILISATION");
            Assert.DoesNotContain(fields, x => x.Code is "productionDate" or "expirationDate" or "expDate" or "expDate72" or "expDateConditions");
            var result = SuzUtilisationInput.Prepare(new("milk", package, "UTILISATION", new Dictionary<string, string>(), [Row(MilkCode())]), Now);
            Assert.True(result.IsValid);
            using var json = JsonDocument.Parse(result.Reports.Single().Json);
            Assert.Equal(1, json.RootElement.GetProperty("attributes").GetProperty("usedInProduction").GetInt32());
            Assert.False(json.RootElement.GetProperty("attributes").TryGetProperty("productionDate", out _));
        }
        var no = SuzUtilisationInput.Prepare(Milk(Row(MilkCode())) with { CommonValues = new Dictionary<string, string> { ["productionDate"] = "2026-10-01", ["expDate"] = "2026-10-10", ["usedInProduction"] = "0" } }, Now);
        using var noJson = JsonDocument.Parse(no.Reports.Single().Json);
        Assert.Equal(0, noJson.RootElement.GetProperty("attributes").GetProperty("usedInProduction").GetInt32());
    }

    [Fact]
    public void DuplicateValuesConflictAndInvalidRowsBlockAllDrafts()
    {
        var result = SuzUtilisationInput.Prepare(Milk(new(MilkCode(), new Dictionary<string, string> { ["3103"] = "001324" }),
            new(MilkCode(), new Dictionary<string, string> { ["3103"] = "002324" })), Now);
        Assert.False(result.IsValid); Assert.Empty(result.Reports); Assert.Contains(result.Errors, x => x.RowNumber == 2);
        var conflict = SuzUtilisationInput.Prepare(Milk(new SuzUtilisationRow(MilkCode() + "\u001d3103001324", new Dictionary<string, string> { ["3103"] = "002324" })), Now);
        Assert.Contains(conflict.Errors, x => x.Field == "3103");
        var shortCode = SuzUtilisationInput.Prepare(Milk(Row("010464001674838921000001")), Now);
        Assert.False(shortCode.IsValid); Assert.Empty(shortCode.Reports);
        Assert.Equal(1, SuzUtilisationInput.Prepare(Milk(Row(MilkCode()), Row(MilkCode())), Now).CodeCount);
    }

    [Fact]
    public void VariableExpiryUsesObjectAndSplitsGtins()
    {
        var values = new Dictionary<string, string> { ["productionDate"] = "2026-10-01", ["expDateConditions"] = "{\"1\":\"2026-10-10\",\"2\":\"2026-11-01\"}" };
        var result = SuzUtilisationInput.Prepare(Milk(Row(MilkCode()), Row(MilkCode(2, "04640016748390"))) with { CommonValues = values }, Now);
        Assert.True(result.IsValid); Assert.Equal(2, result.Reports.Count);
        foreach (var report in result.Reports)
        { using var json = JsonDocument.Parse(report.Json); Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("attributes").GetProperty("expDateConditions").ValueKind); Assert.NotNull(report.Gtin); }
        values["expDateConditions"] = "{\"1\":\"2026-10-10\",\"1\":\"2026-11-01\"}";
        Assert.False(SuzUtilisationInput.Prepare(Milk(Row(MilkCode())) with { CommonValues = values }, Now).IsValid);
        values["expDateConditions"] = "[]";
        Assert.False(SuzUtilisationInput.Prepare(Milk(Row(MilkCode())) with { CommonValues = values }, Now).IsValid);
    }

    [Theory]
    [InlineData("2026-10-31", "2026-12-20", false)]
    [InlineData("2026-11-01", "2026-12-20", true)]
    [InlineData("2027-02-28", "2027-03-10", false)]
    [InlineData("2027-03-01", "2027-03-10", true)]
    public void MilkBatchCalendarRequirements(string today, string expiry, bool required)
    {
        var now = DateTimeOffset.Parse(today + "T12:00:00+03:00");
        var values = new Dictionary<string, string> { ["productionDate"] = today, ["expDate"] = expiry };
        var result = SuzUtilisationInput.Prepare(Milk(Row(MilkCode())) with { CommonValues = values }, now);
        Assert.Equal(required, result.Errors.Any(x => x.Field == "batchNumber"));
        if (required)
        {
            values["batchNumber"] = "04640016748389" + now.ToString("yyMMdd") + "A";
            Assert.True(SuzUtilisationInput.Prepare(Milk(Row(MilkCode())) with { CommonValues = values }, now).IsValid);
            values["batchNumber"] = "04640016748390" + now.ToString("yyMMdd") + "A";
            Assert.Contains(SuzUtilisationInput.Prepare(Milk(Row(MilkCode())) with { CommonValues = values }, now).Errors, x => x.Field == "batchNumber");
        }
    }

    [Fact]
    public void SpecialScenariosRequirePairsAndSuppressOrdinaryAttributes()
    {
        Assert.Equal(new[] { "UTILISATION", "SPLITMARK", "DIVISIONMARK" }, SuzUtilisationRules.Scenarios("radio").Select(x => x.Code));
        Assert.DoesNotContain(SuzUtilisationRules.Fields("chemistry", "UNIT", "REMARK"), x => x.Code is "productionDate" or "expirationDate" or "alcoholVolume");
        var code = "010464001674838921000001\u001d93dGVz";
        var request = new SuzUtilisationRequest("chemistry", "UNIT", "REMARK", new Dictionary<string, string>(), [Row(code + "\u001e010464001674838921000002")]);
        Assert.True(SuzUtilisationInput.Prepare(request, Now).IsValid);
        Assert.False(SuzUtilisationInput.Prepare(request, Now.AddMonths(-3)).IsValid);
        Assert.False(SuzUtilisationInput.Prepare(request with { Rows = [Row(code)] }, Now).IsValid);
        Assert.False(SuzUtilisationInput.Prepare(request with { Rows = [Row(code + "\u001e010464001674838921000002\u001d93dGVz")] }, Now).IsValid);
    }

    [Fact]
    public void NonGsTobaccoAndMedicalFactorySerialFollowDifferentTemplates()
    {
        var values = new Dictionary<string, string> { ["productionDate"] = "2026-10-01T10:00:00+0300", ["_participantKind"] = "LEGAL", ["kpp"] = "123456789" };
        var request = new SuzUtilisationRequest("tobacco", "UNIT", "UTILISATION", values, [Row("04640016748389ABCDEFG1234dGVz")]);
        Assert.True(SuzUtilisationInput.Prepare(request, Now).IsValid);
        values["productionDate"] = "2026-10-01";
        Assert.False(SuzUtilisationInput.Prepare(request, Now).IsValid);
        var medical = "0104640016748389210000000000001\u001d91ABCD\u001d92" + new string('A', 44);
        var serialRows = new[] { new SuzUtilisationRow(medical, new Dictionary<string, string> { ["97"] = "Серия 1" }) };
        var medicalRequest = new SuzUtilisationRequest("wheelchairs", "UNIT", "UTILISATION", new Dictionary<string, string> { ["productionDate"] = "2026-10-01", ["_hasShelfLife"] = "0" }, serialRows);
        var result = SuzUtilisationInput.Prepare(medicalRequest, Now);
        Assert.True(result.IsValid);
        using var json = JsonDocument.Parse(result.Reports.Single().Json);
        Assert.EndsWith("\u001d97" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Серия 1")), json.RootElement.GetProperty("sntins")[0].GetString());
    }

    [Fact]
    public void ConditionalConstructionDatesAndTypedAttributes()
    {
        var request = new SuzUtilisationRequest("construction", "UNIT", "UTILISATION", new Dictionary<string, string> { ["_cementWarranty"] = "1", ["_participantKind"] = "LEGAL", ["kpp"] = "123456789" },
            [Row("0104640016748389210000000000001\u001d93dGVz")]);
        Assert.True(SuzUtilisationInput.Prepare(request, Now).IsValid);
        Assert.False(SuzUtilisationInput.Prepare(request with { CommonValues = new Dictionary<string, string>(request.CommonValues) { ["productionDate"] = "2026-10-01" } }, Now).IsValid);
        var milk = SuzUtilisationInput.Prepare(Milk(Row(MilkCode())) with { CommonValues = new Dictionary<string, string> { ["productionDate"] = "2026-10-01", ["expDate"] = "2026-10-10", ["usedInProduction"] = "1" } }, Now);
        using var json = JsonDocument.Parse(milk.Reports.Single().Json);
        Assert.Equal(1, json.RootElement.GetProperty("attributes").GetProperty("usedInProduction").GetInt32());
    }
}
