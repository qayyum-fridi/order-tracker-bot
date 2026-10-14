using System.IO.Compression;
using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Export;

/// <summary>
/// Reads whatever spreadsheet a seller sends and hands back plain cell text: .xlsx / .xlsm, the old .xls, OpenDocument .ods, and delimited text (.csv / .tsv / .txt).
/// The format is recognised from the bytes (a phone often sends a CSV as "application/vnd.ms-excel" or a generic type), so a wrong extension or MIME type doesn't matter.
/// Anything else (PDF, image, Word, a damaged file) comes back as null.
/// </summary>
public sealed class ImportFileReader : IImportFileReader
{
    private const int MaxEntries = 2000;
    private const long MaxUncompressedBytes = 100L * 1024 * 1024; // a 5 MB upload must not unpack to gigabytes

    private readonly ImportXlsxReader _xlsx;
    private readonly ILogger<ImportFileReader>? _logger;

    public ImportFileReader(ILogger<ImportFileReader>? logger = null, ILogger<ImportXlsxReader>? xlsxLogger = null)
    {
        _logger = logger;
        _xlsx = new ImportXlsxReader(xlsxLogger);
    }

    public IReadOnlyList<ImportGridSheet>? Read(byte[] file, string? fileName = null)
    {
        try
        {
            if (IsZip(file)) return ReadZip(file, fileName);
            if (IsOle(file)) return ImportXlsReader.Read(file);
            return ImportCsvReader.Read(file, fileName);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read the uploaded file.");
            return null;
        }
    }

    private IReadOnlyList<ImportGridSheet>? ReadZip(byte[] file, string? fileName)
    {
        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries || zip.Entries.Sum(e => e.Length) > MaxUncompressedBytes) return null;

        if (zip.GetEntry("xl/workbook.xml") is not null) return _xlsx.Read(file, fileName);
        if (zip.GetEntry("content.xml") is { } content)
        {
            using var stream = content.Open();
            return ImportOdsReader.Read(stream);
        }
        return null; // some other zip (docx, pptx, .xlsb, ...)
    }

    private static bool IsZip(byte[] b) => b.Length > 4 && b[0] == 'P' && b[1] == 'K' && b[2] is 3 or 5;

    // the compound-file signature of Excel 97-2003 (and also of old .doc / .ppt, which the xls reader then refuses)
    private static bool IsOle(byte[] b) =>
        b.Length > 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0 && b[4] == 0xA1 && b[5] == 0xB1 && b[6] == 0x1A && b[7] == 0xE1;
}
