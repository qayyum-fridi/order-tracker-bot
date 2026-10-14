using Microsoft.Extensions.Logging;
using MiniExcelLibs;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Export;

/// <summary>Reads an uploaded .xlsx / .xlsm with MiniExcel. Everything comes back as text, by column position (the sheet's own headings are not interpreted here); formulas are read as their stored values, never executed.</summary>
public sealed class ImportXlsxReader : IImportFileReader
{
    private readonly ILogger<ImportXlsxReader>? _logger;

    public ImportXlsxReader(ILogger<ImportXlsxReader>? logger = null) => _logger = logger;

    public IReadOnlyList<ImportGridSheet>? Read(byte[] file, string? fileName = null)
    {
        try
        {
            using var names = new MemoryStream(file);
            var sheetNames = MiniExcel.GetSheetNames(names);

            var sheets = new List<ImportGridSheet>();
            foreach (var name in sheetNames)
            {
                using var stream = new MemoryStream(file);
                var builder = new ImportGridBuilder(name);
                var number = 0;
                foreach (var raw in MiniExcel.Query(stream, useHeaderRow: false, sheetName: name))
                {
                    number++; // the first row of the sheet is row 1
                    if (raw is not IDictionary<string, object?> cells) continue;

                    var texts = new List<string>();
                    foreach (var (column, value) in cells)
                    {
                        var position = ColumnIndex(column);
                        if (position is < 0 or >= ImportGridBuilder.MaxColumns) continue;
                        var text = ImportGridBuilder.CellText(value);
                        if (text.Length == 0) continue;
                        while (texts.Count <= position) texts.Add("");
                        texts[position] = text;
                    }
                    builder.Add(number, texts);
                }
                sheets.Add(builder.Build());
            }
            return sheets;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read the uploaded workbook.");
            return null;
        }
    }

    /// <summary>"A" -> 0, "B" -> 1, "AA" -> 26. MiniExcel names the columns of a sheet read without a heading row that way.</summary>
    private static int ColumnIndex(string letters)
    {
        var index = 0;
        foreach (var ch in letters)
        {
            if (ch is < 'A' or > 'Z') return -1;
            index = index * 26 + (ch - 'A' + 1);
        }
        return index - 1;
    }
}
