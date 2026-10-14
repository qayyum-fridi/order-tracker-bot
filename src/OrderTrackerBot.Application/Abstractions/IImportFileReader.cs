namespace OrderTrackerBot.Application.Abstractions;

/// <summary>One non-blank worksheet row exactly as it sits in the file: its row number as Excel shows it (heading row = 1) and the cell texts by column position ("" for an empty cell).</summary>
public sealed record ImportGridRow(int Number, IReadOnlyList<string> Cells);

public sealed record ImportGridSheet(string Name, IReadOnlyList<ImportGridRow> Rows);

/// <summary>One non-blank data row after <c>ImportColumnMapper</c> has renamed the seller's columns to the bot's own headings: row number as Excel shows it and heading -> cell text (headings matched case-insensitively).</summary>
public sealed record ImportRow(int Number, IReadOnlyDictionary<string, string> Cells);

public sealed record ImportSheet(string Name, IReadOnlyList<ImportRow> Rows);

public interface IImportFileReader
{
    /// <summary>
    /// Reads every sheet of a spreadsheet file as plain cell text, or returns null when the file isn't a readable spreadsheet. The format (.xlsx, .xls, .ods, .csv, .tsv)
    /// is recognised from the bytes, not the name; <paramref name="fileName"/> only names the single sheet of a CSV. Cells are never evaluated as formulas.
    /// </summary>
    IReadOnlyList<ImportGridSheet>? Read(byte[] file, string? fileName = null);
}
