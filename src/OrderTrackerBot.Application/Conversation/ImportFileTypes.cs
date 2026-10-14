namespace OrderTrackerBot.Application.Conversation;

/// <summary>Which uploaded documents the import accepts. Only a first filter on name / MIME type: the reader recognises the real format from the file's bytes.</summary>
public static class ImportFileTypes
{
    private static readonly string[] Extensions = { ".xlsx", ".xlsm", ".xls", ".ods", ".csv", ".tsv", ".txt" };

    private static readonly string[] MimeTypes =
    {
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.ms-excel.sheet.macroenabled.12",
        "application/vnd.ms-excel", "application/vnd.oasis.opendocument.spreadsheet",
        "text/csv", "application/csv", "text/comma-separated-values", "text/tab-separated-values", "text/plain"
    };

    public static bool IsSupported(string? fileName, string? mimeType) =>
        (fileName is not null && Extensions.Any(e => fileName.Trim().EndsWith(e, StringComparison.OrdinalIgnoreCase))) ||
        (mimeType is not null && MimeTypes.Contains(mimeType.Split(';')[0].Trim(), StringComparer.OrdinalIgnoreCase));
}
