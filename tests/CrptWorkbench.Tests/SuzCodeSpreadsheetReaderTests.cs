using System.IO.Compression;
using System.Text;
using CrptWorkbench.Application;
using CrptWorkbench.Infrastructure;
using Xunit;

namespace CrptWorkbench.Tests;

public sealed class SuzCodeSpreadsheetReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExportedWorkbookReadsFirstSheetTextCodesAndIgnoresOtherColumns()
    {
        await using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "suz-codes.xlsx"));
        var values = await new SuzCodeSpreadsheetReader().ReadFirstColumnAsync(file, Ct);
        Assert.Equal(["code", "010464001674838921000001", "010464001674838921000002"], values);
    }

    [Fact]
    public async Task FirstSheetReadsOnlyTextFirstColumnAndPreservesSeparators()
    {
        await using var file = Workbook("""
            <row r="1"><c r="A1" t="inlineStr"><is><t>code</t></is></c></row>
            <row r="2"><c r="A2" t="s"><v>0</v></c><c r="B2" t="n"><v>42</v></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>010464001674838921000002_x001D_93tail</t></is></c></row>
            """);
        var values = await new SuzCodeSpreadsheetReader().ReadFirstColumnAsync(file, Ct);
        var rows = SuzUtilisationInput.Codes(values);
        Assert.Equal(2, rows.Count);
        Assert.Equal("010464001674838921000001\u001d93tail", rows[0].Code);
        Assert.Equal("010464001674838921000002\u001d93tail", rows[1].Code);
    }

    [Theory]
    [InlineData("<c r=\"A1\" t=\"n\"><v>123</v></c>")]
    [InlineData("<c r=\"A1\" t=\"str\"><f>A2</f><v>code</v></c>")]
    public async Task FirstColumnRejectsNumbersAndFormulas(string cell)
    {
        await using var file = Workbook("<row r=\"1\">" + cell + "</row>");
        await Assert.ThrowsAsync<FormatException>(() => new SuzCodeSpreadsheetReader().ReadFirstColumnAsync(file, Ct));
    }

    [Fact]
    public async Task InvalidWorkbookIsRejected()
    {
        await using var file = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<FormatException>(() => new SuzCodeSpreadsheetReader().ReadFirstColumnAsync(file, Ct));
    }

    private static MemoryStream Workbook(string rows)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Codes\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Add(zip, "xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + rows + "</sheetData></worksheet>");
            Add(zip, "xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>010464001674838921000001_x001D_93tail</t></si></sst>");
        }
        stream.Position = 0;
        return stream;
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(xml);
    }
}
