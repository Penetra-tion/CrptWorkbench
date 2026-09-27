using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class CodeCorrectionRulesTests
{
    [Fact]
    public void SupportedGroupsUseProtocolIdentifiersAndExcludeUnapprovedGroups()
    {
        Assert.Equal(37, CodeCorrectionRules.Groups.Count);
        Assert.Equal(37, CodeCorrectionRules.Groups.Select(x => x.Id).Distinct().Count());
        Assert.Equal(40, CodeCorrectionRules.Groups.Single(x => x.Code == "fire").Id);
        Assert.Equal(59, CodeCorrectionRules.Groups.Single(x => x.Code == "pyrotechnics").Id);
        Assert.Equal(14, CodeCorrectionRules.Groups.Single(x => x.Code == "furs").Id);
        Assert.Equal(49, CodeCorrectionRules.Groups.Single(x => x.Code == "furslp").Id);
        foreach (var group in new[] { "alcohol", "books", "heater", "nicotindev", "homeware", "unknown", "MILK" })
            Assert.All(CodeCorrectionRules.Kinds, kind => Assert.False(CodeCorrectionRules.Supports(group, kind.Code)));
    }

    [Theory]
    [InlineData("vsd", "conserve petfood milk seafood meat frozen sweets")]
    [InlineData("expirationDate", "antiseptic grocery nabeer vetpharma toys conserve petfood chemistry wheelchairs milk seafood autofluids meat beer pyrotechnics frozen vegetableoil sweets softdrinks bio fire construction water")]
    [InlineData("variableExpiration", "milk meat frozen sweets")]
    [InlineData("weight", "milk seafood meat frozen")]
    [InlineData("licences", "water")]
    [InlineData("partNumber", "milk")]
    public void PositiveListsExactlyMatchReference(string kind, string permitted)
    {
        var expected = permitted.Split(' ').ToHashSet();
        foreach (var group in CodeCorrectionRules.Groups)
            Assert.Equal(expected.Contains(group.Code), CodeCorrectionRules.Supports(group.Code, kind));
    }

    [Theory]
    [InlineData("productionDate", "")]
    [InlineData("tnved", "otp nabeer ncp beer tobacco")]
    [InlineData("permitDocs", "vetpharma bio")]
    [InlineData("emissionType", "beer radio gadgets sweets furs")]
    public void ExclusionsExactlyMatchReference(string kind, string excluded)
    {
        var denied = excluded.Split(' ').ToHashSet();
        foreach (var group in CodeCorrectionRules.Groups)
            Assert.Equal(!denied.Contains(group.Code), CodeCorrectionRules.Supports(group.Code, kind));
        Assert.False(CodeCorrectionRules.Supports("milk", "unknown"));
    }

    [Theory]
    [InlineData("vsd", "INTRODUCED RETIRED WRITTEN_OFF", "UNIT")]
    [InlineData("productionDate", "APPLIED INTRODUCED", "UNIT BUNDLE")]
    [InlineData("expirationDate", "APPLIED INTRODUCED", "UNIT")]
    [InlineData("variableExpiration", "APPLIED INTRODUCED", "UNIT")]
    [InlineData("weight", "APPLIED INTRODUCED", "UNIT")]
    [InlineData("tnved", "INTRODUCED", "UNIT BUNDLE")]
    [InlineData("permitDocs", "APPLIED INTRODUCED", "UNIT BUNDLE")]
    [InlineData("licences", "APPLIED INTRODUCED", "UNIT")]
    [InlineData("emissionType", "EMITTED APPLIED", "UNIT BUNDLE")]
    [InlineData("partNumber", "APPLIED", "UNIT")]
    public void StatusAndPackagingRestrictionsMatchEachField(string kind, string statuses, string packages)
    {
        Assert.Equal(statuses.Split(' '), CodeCorrectionRules.AllowedStatuses(kind));
        Assert.Equal(packages.Split(' '), CodeCorrectionRules.AllowedPackageTypes(kind));
        Assert.Empty(CodeCorrectionRules.AllowedStatuses("unknown"));
        Assert.Empty(CodeCorrectionRules.AllowedPackageTypes("unknown"));
    }

    [Fact]
    public void MovingStateExceptionAppliesOnlyToBeerPermitDocuments()
    {
        foreach (var group in CodeCorrectionRules.Groups)
        foreach (var kind in CodeCorrectionRules.Kinds.Where(x => CodeCorrectionRules.Supports(group.Code, x.Code)))
        {
            Assert.True(CodeCorrectionRules.AllowsSpecialState(group.Code, kind.Code, null));
            Assert.True(CodeCorrectionRules.AllowsSpecialState(group.Code, kind.Code, "EMPTY"));
            Assert.True(CodeCorrectionRules.AllowsSpecialState(group.Code, kind.Code, "WAIT_TRANSFER_TO_OWNER"));
            Assert.Equal(group.Code == "beer" && kind.Code == "permitDocs",
                CodeCorrectionRules.AllowsSpecialState(group.Code, kind.Code, "MOVING_BY_UD"));
            Assert.False(CodeCorrectionRules.AllowsSpecialState(group.Code, kind.Code, "UNKNOWN_STATE"));
        }
    }

    [Fact]
    public void PermitTypesRespectDocumentSpecificFootnoteAndGroupExceptions()
    {
        Assert.Equal(new[] { 2 }, CodeCorrectionRules.AllowedPermitTypes("beer"));
        Assert.Equal(new[] { 2 }, CodeCorrectionRules.AllowedPermitTypes("petfood"));
        foreach (var group in new[] { "nabeer", "bicycle", "shoes", "fire", "furs", "electronics", "tires" })
            Assert.Equal(new[] { 1, 2 }, CodeCorrectionRules.AllowedPermitTypes(group));
        foreach (var group in new[] { "wheelchairs", "milk", "furslp", "pyrotechnics" })
            Assert.Equal(new[] { 1, 2, 4 }, CodeCorrectionRules.AllowedPermitTypes(group));
        foreach (var group in new[] { "vetpharma", "bio", "unknown" })
            Assert.Empty(CodeCorrectionRules.AllowedPermitTypes(group));
        Assert.All(CodeCorrectionRules.Groups, group => Assert.DoesNotContain(5, CodeCorrectionRules.AllowedPermitTypes(group.Code)));
    }

    [Fact]
    public void AggregationAndEmissionRestrictionsRemainFieldSpecific()
    {
        Assert.Equal(new[] { "GROUP" }, CodeCorrectionRules.ForbiddenParentPackageTypes("vsd"));
        Assert.Equal(new[] { "GROUP" }, CodeCorrectionRules.ForbiddenParentPackageTypes("tnved"));
        Assert.Equal(new[] { "GROUP", "SET" }, CodeCorrectionRules.ForbiddenParentPackageTypes("partNumber"));
        Assert.True(CodeCorrectionRules.RequiresNoParent("emissionType"));
        Assert.False(CodeCorrectionRules.RequiresNoParent("vsd"));
        Assert.False(CodeCorrectionRules.AllowsEmissionType("tnved", "FOREIGN"));
        Assert.False(CodeCorrectionRules.AllowsEmissionType("tnved", null));
        Assert.False(CodeCorrectionRules.AllowsEmissionType("tnved", "UNKNOWN"));
        Assert.True(CodeCorrectionRules.AllowsEmissionType("tnved", "LOCAL"));
        Assert.True(CodeCorrectionRules.AllowsEmissionType("emissionType", "FOREIGN"));
        Assert.False(CodeCorrectionRules.AllowsEmissionType("emissionType", "REAPPLY"));
        Assert.True(CodeCorrectionRules.AllowsEarlyProductionDate("gadgets", "REAPPLY"));
        Assert.False(CodeCorrectionRules.AllowsEarlyProductionDate("milk", "REAPPLY"));
        Assert.False(CodeCorrectionRules.AllowsEarlyProductionDate("toys", "LOCAL"));
    }

    [Fact]
    public void GuidanceExplainsRemoteAuthorityAndApplicableConditions()
    {
        foreach (var group in CodeCorrectionRules.Groups)
        foreach (var kind in CodeCorrectionRules.Kinds.Where(x => CodeCorrectionRules.Supports(group.Code, x.Code)))
        {
            var text = CodeCorrectionRules.Describe(group.Code, kind.Code);
            Assert.Contains("Статус:", text);
            Assert.Contains("право участника проверяет ЦРПТ", text);
        }
        Assert.Contains("ВетИС", CodeCorrectionRules.Describe("milk", "vsd"));
        Assert.Contains("Фиксация", CodeCorrectionRules.Describe("beer", "permitDocs"));
        Assert.DoesNotContain("Фиксация", CodeCorrectionRules.Describe("nabeer", "permitDocs"));
    }
}
