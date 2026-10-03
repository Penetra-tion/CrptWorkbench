using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CrptWorkbench.Application;

namespace CrptWorkbench.Infrastructure;

public sealed class SuzCodeSpreadsheetReader : ISuzCodeSpreadsheetReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public async Task<IReadOnlyList<string>> ReadFirstColumnAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken);
            copy.Position = 0;
            using var zip = new ZipArchive(copy, ZipArchiveMode.Read, true);
            var workbook = Load(zip, "xl/workbook.xml");
            var relationshipId = (string?)workbook.Descendants(Main + "sheet").FirstOrDefault()?.Attribute(Relationships + "id")
                ?? throw new FormatException("В XLSX нет первого листа.");
            var relations = Load(zip, "xl/_rels/workbook.xml.rels");
            var target = (string?)relations.Descendants(PackageRelationships + "Relationship")
                .FirstOrDefault(x => (string?)x.Attribute("Id") == relationshipId)?.Attribute("Target")
                ?? throw new FormatException("Не найден первый лист XLSX.");
            var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (sheetPath.Contains("..", StringComparison.Ordinal) || !sheetPath.StartsWith("xl/", StringComparison.Ordinal))
                throw new FormatException("Неверный путь первого листа XLSX.");
            var sheet = Load(zip, sheetPath);
            var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");
            var shared = sharedEntry is null ? [] : Load(zip, "xl/sharedStrings.xml").Descendants(Main + "si")
                .Select(x => string.Concat(x.Descendants(Main + "t").Select(t => t.Value))).ToArray();
            var codes = new List<string>();
            foreach (var row in sheet.Descendants(Main + "sheetData").Elements(Main + "row"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cell = row.Elements(Main + "c").FirstOrDefault(x =>
                {
                    var reference = (string?)x.Attribute("r");
                    return reference is { Length: > 1 } && reference[0] == 'A' && char.IsAsciiDigit(reference[1]);
                });
                if (cell is null) continue;
                if (cell.Element(Main + "f") is not null) throw new FormatException("Формулы в первом столбце XLSX не поддерживаются.");
                var type = (string?)cell.Attribute("t");
                if (type is not ("s" or "inlineStr" or "str"))
                    throw new FormatException("Коды в первом столбце XLSX должны быть текстовыми ячейками.");
                var value = type switch
                {
                    "s" when int.TryParse(cell.Element(Main + "v")?.Value, out var index) && index >= 0 && index < shared.Length => shared[index],
                    "inlineStr" => string.Concat(cell.Descendants(Main + "t").Select(x => x.Value)),
                    "str" => cell.Element(Main + "v")?.Value ?? "",
                    _ => throw new FormatException("Неверная текстовая ячейка XLSX.")
                };
                codes.Add(Regex.Replace(value, "_x([0-9A-Fa-f]{4})_", x => ((char)Convert.ToInt32(x.Groups[1].Value, 16)).ToString()));
            }
            return codes;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or ArgumentException)
        {
            throw new FormatException("Не удалось прочитать XLSX. Проверьте файл.");
        }
    }

    private static XDocument Load(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new FormatException("В XLSX отсутствует обязательная часть.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
