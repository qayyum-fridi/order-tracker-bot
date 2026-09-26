using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Catalog;

/// <summary>
/// Reads a publicly-shared Google Sheet as CSV — no Google API credentials needed, just the
/// "Anyone with the link can view" export endpoint. Expects one product per row: name in the
/// first column (unit hints like "Sugar 5 kg" are parsed the same as a typed catalog line),
/// price in the second. Non-numeric second columns (e.g. a header row) are skipped.
/// </summary>
public class CatalogSheetImporter : ICatalogSheetImporter
{
    private static readonly Regex SheetId = new(@"/spreadsheets/d/([A-Za-z0-9_-]+)", RegexOptions.Compiled);
    private static readonly Regex Gid = new(@"[?&]gid=(\d+)", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<CatalogSheetImporter> _logger;

    public CatalogSheetImporter(HttpClient httpClient, ILogger<CatalogSheetImporter> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>?> FetchProductLinesAsync(string sheetUrl, CancellationToken cancellationToken = default)
    {
        var idMatch = SheetId.Match(sheetUrl);
        if (!idMatch.Success) return null;

        var gidMatch = Gid.Match(sheetUrl);
        var exportUrl = $"https://docs.google.com/spreadsheets/d/{idMatch.Groups[1].Value}/export?format=csv"
            + (gidMatch.Success ? $"&gid={gidMatch.Groups[1].Value}" : "");

        string csv;
        try
        {
            csv = await _httpClient.GetStringAsync(exportUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch catalog sheet {Url}.", sheetUrl);
            return null;
        }

        var lines = new List<string>();
        foreach (var row in csv.Split('\n'))
        {
            var cols = row.Split(',');
            if (cols.Length < 2) continue;
            var name = cols[0].Trim().Trim('"');
            var priceText = cols[1].Trim().Trim('"');
            if (name.Length == 0 || !decimal.TryParse(priceText, out var price)) continue;
            lines.Add($"{name} - {price}");
        }

        return lines;
    }
}
