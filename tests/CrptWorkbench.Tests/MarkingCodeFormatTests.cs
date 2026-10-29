using CrptWorkbench.Domain;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class MarkingCodeFormatTests
{
    [Theory]
    [InlineData("(01)04607167620070(21)SERIAL", true)]
    [InlineData("0104607167620070(21)SERIAL", true)]
    [InlineData("(01)0460716762007021SERIAL", true)]
    [InlineData("(21)SERIAL0000000000", true)]
    [InlineData("0104607167620070215ZsCJggFr(01)", false)]
    [InlineData("0104607167626157215a9rD(21)KcO*", false)]
    [InlineData("010460716762007021(21)SERIAL", false)]
    [InlineData("010460716762007021(01)SERIAL", false)]
    [InlineData("000000000000000001", false)]
    [InlineData("", false)]
    public void OnlyApplicationIdentifierPositionsAreRejected(string code, bool expected) =>
        Assert.Equal(expected, MarkingCodeFormat.HasBracketedApplicationIdentifiers(code));
}
