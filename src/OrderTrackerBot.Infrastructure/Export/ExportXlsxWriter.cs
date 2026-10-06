using System.Data;
using MiniExcelLibs;
using MiniExcelLibs.OpenXml;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Export;

/// <summary>Writes export sheets with MiniExcel (Apache-2.0). Strings stay strings (no formula evaluation), so buyer-supplied text can't inject formulas.</summary>
public sealed class ExportXlsxWriter : IExportFileWriter
{
    public byte[] WriteXlsx(IReadOnlyList<ExportSheet> sheets)
    {
        var workbook = new Dictionary<string, object>();
        foreach (var sheet in sheets)
        {
            var table = new DataTable(sheet.Name);
            foreach (var column in sheet.Columns) table.Columns.Add(column.Header, column.Type);
            foreach (var row in sheet.Rows) table.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
            workbook[sheet.Name] = table;
        }

        using var stream = new MemoryStream();
        MiniExcel.SaveAs(stream, workbook, configuration: new OpenXmlConfiguration { AutoFilter = true, FastMode = true, EnableAutoWidth = true });
        return stream.ToArray();
    }
}
