using System.Globalization;
using Microsoft.Extensions.Logging;
using MiniExcelLibs;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Export;

/// <summary>Reads an uploaded .xlsx with MiniExcel. Everything comes back as text; formulas are read as their stored values, never executed.</summary>
public sealed class ImportXlsxReader : IImportFileReader
{
    private readonly ILogger<ImportXlsxReader>? _logger;

    public ImportXlsxReader(ILogger<ImportXlsxReader>? logger = null) => _logger = logger;

    public IReadOnlyList<ImportSheet>? Read(byte[] xlsx)
    {
        try
        {
            using var names = new MemoryStream(xlsx);
            var sheetNames = MiniExcel.GetSheetNames(names);

            var sheets = new List<ImportSheet>();
            foreach (var name in sheetNames)
            {
                using var stream = new MemoryStream(xlsx);
                var rows = new List<ImportRow>();
                var index = 0;
                foreach (var raw in MiniExcel.Query(stream, useHeaderRow: true, sheetName: name))
                {
                    var number = index++ + 2; // the heading row is row 1
                    if (raw is not IDictionary<string, object?> cells) continue;
                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (header, value) in cells)
                    {
                        var key = header.Trim();
                        var text = CellText(value);
                        if (key.Length > 0 && text.Length > 0) row[key] = text;
                    }
                    if (row.Count > 0) rows.Add(new ImportRow(number, row));
                }
                sheets.Add(new ImportSheet(name, rows));
            }
            return sheets;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read the uploaded workbook.");
            return null;
        }
    }

    private static string CellText(object? value) => value switch
    {
        null => "",
        string s => s.Trim(),
        // 3001234567 arrives as a double: no exponent, no ".0"
        double d => d.ToString("0.############", CultureInfo.InvariantCulture),
        float f => f.ToString("0.######", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.############", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture)?.Trim() ?? "",
        _ => value.ToString()?.Trim() ?? ""
    };
}
