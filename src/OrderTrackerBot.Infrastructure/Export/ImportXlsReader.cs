using System.Text;
using ExcelDataReader;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Export;

/// <summary>The old binary Excel format (.xls, Excel 97-2003) with ExcelDataReader. Values only; nothing is evaluated.</summary>
internal static class ImportXlsReader
{
    static ImportXlsReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // .xls text is stored in code pages

    public static IReadOnlyList<ImportGridSheet> Read(byte[] file)
    {
        using var stream = new MemoryStream(file);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream);

        var sheets = new List<ImportGridSheet>();
        do
        {
            var builder = new ImportGridBuilder(reader.Name);
            var number = 0;
            while (reader.Read())
            {
                number++;
                var texts = new List<string>();
                for (var i = 0; i < Math.Min(reader.FieldCount, ImportGridBuilder.MaxColumns); i++)
                    texts.Add(ImportGridBuilder.CellText(reader.GetValue(i)));
                builder.Add(number, texts);
            }
            sheets.Add(builder.Build());
        } while (reader.NextResult());
        return sheets;
    }
}
