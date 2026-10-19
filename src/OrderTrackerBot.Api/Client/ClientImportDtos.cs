using System.Globalization;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Conversation;

namespace OrderTrackerBot.Api.Client;

public static class ClientImportLimits
{
    /// <summary>Same cap as the WhatsApp import.</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>The file plus its form fields.</summary>
    public const int MaxUploadBytes = MaxFileBytes + 64 * 1024;
}

public sealed record ClientImportPreviewDto(
    string Id,
    string Kind,
    string FileName,
    IReadOnlyList<ImportColumnDto> MappedColumns,
    IReadOnlyList<string> IgnoredColumns,
    int RowCount,
    int ValidRowCount,
    IReadOnlyList<ImportErrorDto> Errors,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Sample)
{
    private static readonly Regex RowNumber = new(@"row (\d+)", RegexOptions.Compiled);

    public static ClientImportPreviewDto From(string id, string kind, string fileName, ClientImportPreview preview) => new(
        id, kind, fileName,
        preview.MappedColumns.Select(c => new ImportColumnDto(c.Source, c.Field)).ToList(),
        preview.IgnoredColumns,
        preview.RowCount,
        preview.NewCount,
        preview.Errors.Select(e => new ImportErrorDto(RowOf(e), e)).ToList(),
        preview.Sample);

    private static int RowOf(string error) =>
        RowNumber.Match(error) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
}

/// <summary>Column in the file (<paramref name="Source"/>) and the field it was read into.</summary>
public sealed record ImportColumnDto(string Source, string Field);

public sealed record ImportErrorDto(int Row, string Message);

public sealed record ClientImportResultDto(int Created, int Updated, int Skipped);
