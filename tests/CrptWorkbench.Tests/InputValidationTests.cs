using CrptWorkbench.Application;
using CrptWorkbench.Domain;
using CrptWorkbench.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class InputValidationTests
{
    [Theory]
    [InlineData("", "Name")]
    [InlineData(" \t ", "Name")]
    [InlineData("name\n", "Name")]
    [InlineData("123456789", "Inn")]
    [InlineData("123456789A", "Inn")]
    public void ProfileRejectsInvalidNameAndInn(string value, string field)
    {
        var profile = new CrptProfile { Name = field == "Name" ? value : "Valid", Inn = field == "Inn" ? value : null };
        Assert.NotNull(InputValidation.Profile(profile).ErrorFor(field));
    }

    [Fact]
    public void ProfileAcceptsInnWithoutCheckingControlDigitsAndLengthLimits()
    {
        Assert.True(InputValidation.Profile(new CrptProfile { Name = new string('a', 200), Inn = "0000000001" }).IsValid);
        Assert.True(InputValidation.Profile(new CrptProfile { Name = "Valid", Inn = "000000000001" }).IsValid);
        Assert.NotNull(InputValidation.Profile(new CrptProfile { Name = new string('a', 201) }).ErrorFor("Name"));
    }

    [Theory]
    [InlineData("ftp://example.org")]
    [InlineData("https://name:secret@example.org")]
    [InlineData("https://example.org/?token=secret")]
    [InlineData("https://example.org/#fragment")]
    [InlineData("not an address")]
    public void ProfileRejectsInvalidUrlWithoutEchoingIt(string value)
    {
        var result = InputValidation.Profile(new CrptProfile { Name = "Valid", SuzProductionBaseUrl = value });
        Assert.NotNull(result.ErrorFor("SuzProductionBaseUrl"));
        Assert.DoesNotContain(value, result.ErrorFor("SuzProductionBaseUrl")!);
    }

    [Fact]
    public void ProfileChecksSuzIdentifiersAndProvider()
    {
        var profile = new CrptProfile { Name = "Valid", SuzOmsId = "bad", SuzConnectionId = "invalid space", TraceXDatabaseProvider = (TraceXDatabaseProvider)99 };
        var result = InputValidation.Profile(profile);
        Assert.NotNull(result.ErrorFor("SuzOmsId"));
        Assert.NotNull(result.ErrorFor("SuzConnectionId"));
        Assert.NotNull(result.ErrorFor("TraceXDatabaseProvider"));
        profile.SuzOmsId = Guid.NewGuid().ToString();
        profile.SuzConnectionId = "connection_123";
        profile.TraceXDatabaseProvider = TraceXDatabaseProvider.SqlServer;
        Assert.True(InputValidation.Profile(profile).IsValid);
    }

    [Fact]
    public void OperationValidatesEveryInputWithoutCheckDigits()
    {
        Assert.True(InputValidation.Operation(NationalCatalogProductHandler.OperationId, new Dictionary<string, string> { ["gtin"] = "00000001" }).IsValid);
        Assert.NotNull(InputValidation.Operation(NationalCatalogProductHandler.OperationId, new Dictionary<string, string> { ["gtin"] = "٠٠٠٠٠٠٠١" }).ErrorFor("gtin"));
        Assert.NotNull(InputValidation.Operation(TrueApiCodeInfoHandler.OperationId, new Dictionary<string, string> { ["codes"] = "short" }).ErrorFor("codes"));
        Assert.NotNull(InputValidation.Operation(TrueApiDocumentStatusHandler.OperationId, new Dictionary<string, string> { ["documentId"] = "valid", ["productGroup"] = "other" }).ErrorFor("productGroup"));
        Assert.NotNull(InputValidation.Operation("suz.order-status", new Dictionary<string, string> { ["value"] = "a\n" }).ErrorFor("value"));
        Assert.True(InputValidation.Operation("suz.order-status", new Dictionary<string, string> { ["value"] = new string('a', 256) }).IsValid);
    }

    [Fact]
    public void TraceXValidatorDoesNotExposeSecrets()
    {
        var validator = new TraceXConnectionStringValidator();
        const string invalid = "Password=secret;this is invalid";
        var result = validator.Validate(TraceXDatabaseProvider.SqlServer, invalid);
        Assert.NotNull(result.ErrorFor("TraceXConnection"));
        Assert.DoesNotContain("secret", result.ErrorFor("TraceXConnection")!);
        Assert.True(validator.Validate(TraceXDatabaseProvider.PostgreSql, "Host=localhost;Username=user;Password=secret").IsValid);
    }
}
