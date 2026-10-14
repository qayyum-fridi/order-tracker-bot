using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Export;

/// <summary>OpenDocument spreadsheets (.ods: LibreOffice, Google Sheets "Download as", Numbers via export). Streams content.xml one row at a time, with DTDs refused; repeated empty rows / columns are skipped, not expanded.</summary>
internal static class ImportOdsReader
{
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    public static IReadOnlyList<ImportGridSheet> Read(Stream content)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(content, settings);

        var builders = new List<ImportGridBuilder>();
        ImportGridBuilder? current = null;
        var rowNumber = 0;
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == Table.NamespaceName)
            {
                if (reader.LocalName == "table")
                {
                    current = new ImportGridBuilder(reader.GetAttribute("name", Table.NamespaceName) ?? $"Sheet{builders.Count + 1}");
                    builders.Add(current);
                    rowNumber = 0;
                }
                else if (reader.LocalName == "table-row" && current is not null)
                {
                    var repeat = Repeat(reader.GetAttribute("number-rows-repeated", Table.NamespaceName));
                    var row = (XElement)XNode.ReadFrom(reader); // moves the reader past the row
                    var cells = Cells(row);
                    if (cells.Any(c => c.Length > 0))
                        for (var i = 0; i < Math.Min(repeat, ImportGridBuilder.MaxRows); i++) current.Add(rowNumber + i + 1, cells);
                    rowNumber += repeat;
                    continue;
                }
            }
            reader.Read();
        }
        return builders.Select(b => b.Build()).ToList();
    }

    private static int Repeat(string? attribute) => int.TryParse(attribute, out var n) && n > 0 ? n : 1;

    private static List<string> Cells(XElement row)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements().Where(e => e.Name.Namespace == Table && e.Name.LocalName is "table-cell" or "covered-table-cell"))
        {
            var repeat = Repeat((string?)cell.Attribute(Table + "number-columns-repeated"));
            var text = CellText(cell);
            if (text.Length == 0)
            {
                // an empty cell may repeat thousands of times (a row's unused columns): move on instead of storing them
                if (cells.Count + repeat > ImportGridBuilder.MaxColumns) return cells;
                for (var i = 0; i < repeat; i++) cells.Add("");
                continue;
            }
            for (var i = 0; i < Math.Min(repeat, ImportGridBuilder.MaxColumns - cells.Count); i++) cells.Add(text);
            if (cells.Count >= ImportGridBuilder.MaxColumns) return cells;
        }
        return cells;
    }

    private static string CellText(XElement cell)
    {
        var type = (string?)cell.Attribute(Office + "value-type");
        switch (type)
        {
            case "float" or "percentage" or "currency":
                var number = (string?)cell.Attribute(Office + "value");
                return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? ImportGridBuilder.CellText(d) : number ?? "";
            case "date": return ((string?)cell.Attribute(Office + "date-value") ?? "").Replace('T', ' ');
            case "time": return (string?)cell.Attribute(Office + "time-value") ?? "";
            case "boolean": return (string?)cell.Attribute(Office + "boolean-value") ?? "";
            default:
                if ((string?)cell.Attribute(Office + "string-value") is { } direct) return direct.Trim();
                return string.Join("\n", cell.Elements(Text + "p").Select(p => Flatten(p).Trim())).Trim();
        }
    }

    private static string Flatten(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText t: sb.Append(t.Value); break;
                case XElement e when e.Name == Text + "s": sb.Append(' ', Math.Clamp(Repeat((string?)e.Attribute(Text + "c")), 1, 50)); break;
                case XElement e when e.Name == Text + "tab": sb.Append('\t'); break;
                case XElement e when e.Name == Text + "line-break": sb.Append('\n'); break;
                case XElement e: sb.Append(Flatten(e)); break; // text:span, text:a, ...
            }
        }
        return sb.ToString();
    }
}
