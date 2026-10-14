using System.Globalization;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Export;

/// <summary>Collects the rows of one sheet for the import readers: cell text trimmed, trailing blanks dropped, blank rows skipped (their numbers still counted by the caller), and size capped.</summary>
internal sealed class ImportGridBuilder
{
    /// <summary>More columns than this is not a customer / product list; the rest of the row is ignored.</summary>
    public const int MaxColumns = 200;
    /// <summary>Well above the 5000 data rows an import accepts, so "too many rows" is still reported, but a runaway sheet can't fill memory.</summary>
    public const int MaxRows = 20000;

    private readonly string _name;
    private readonly List<ImportGridRow> _rows = new();

    /// <summary>A sheet name is echoed back to the seller ("Sheet \"Products\" ..."); a file name used as one can be very long.</summary>
    private const int MaxNameLength = 40;

    public ImportGridBuilder(string name) => _name = name.Length > MaxNameLength ? name[..MaxNameLength] + "…" : name;

    public void Add(int number, IReadOnlyList<string> cells)
    {
        if (_rows.Count >= MaxRows) return;
        var count = Math.Min(cells.Count, MaxColumns);
        while (count > 0 && cells[count - 1].Length == 0) count--;
        if (count == 0) return;
        _rows.Add(new ImportGridRow(number, cells.Take(count).ToList()));
    }

    public ImportGridSheet Build() => new(_name, _rows);

    /// <summary>A cell value as the text the seller sees: numbers without exponent or ".0" (3001234567 stays 3001234567), dates as yyyy-MM-dd.</summary>
    public static string CellText(object? value) => value switch
    {
        null => "",
        string s => s.Trim(),
        double d => d.ToString("0.############", CultureInfo.InvariantCulture),
        float f => f.ToString("0.######", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.############", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture)?.Trim() ?? "",
        _ => value.ToString()?.Trim() ?? ""
    };
}
