namespace OrderTrackerBot.Application.Abstractions;

/// <summary>One non-blank worksheet row: its row number as Excel shows it (heading row = 1) and heading -> cell text (headings matched case-insensitively).</summary>
public sealed record ImportRow(int Number, IReadOnlyDictionary<string, string> Cells);

public sealed record ImportSheet(string Name, IReadOnlyList<ImportRow> Rows);

public interface IImportFileReader
{
    /// <summary>Reads every sheet of an .xlsx file as text, or returns null when the file isn't a readable workbook. Cells are never evaluated as formulas.</summary>
    IReadOnlyList<ImportSheet>? Read(byte[] xlsx);
}
