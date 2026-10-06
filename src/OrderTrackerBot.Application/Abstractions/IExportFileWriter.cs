namespace OrderTrackerBot.Application.Abstractions;

public sealed record ExportColumn(string Header, Type Type);

/// <summary>One worksheet: typed columns (so dates/amounts stay real Excel dates/numbers) and its rows.</summary>
public sealed record ExportSheet(string Name, IReadOnlyList<ExportColumn> Columns, IReadOnlyList<object?[]> Rows);

public interface IExportFileWriter
{
    /// <summary>Writes the sheets as one .xlsx workbook, in the given order.</summary>
    byte[] WriteXlsx(IReadOnlyList<ExportSheet> sheets);
}
