using Microsoft.EntityFrameworkCore;

namespace OrderTrackerBot.Infrastructure.Persistence;

/// <summary>
/// EnsureCreated never alters an existing Sqlite file, so columns added to the model after first deploy would be
/// missing in production. This adds any missing nullable columns; it is additive and safe to run on every start.
/// </summary>
public static class SqliteSchemaPatcher
{
    private static readonly (string Table, string Column, string SqlType)[] Columns =
    {
        ("Products", "Category", "TEXT"), ("Products", "Size", "TEXT"), ("Products", "Color", "TEXT"),
        ("Products", "Sku", "TEXT"), ("Products", "StockQty", "INTEGER"),
        ("Customers", "City", "TEXT"), ("Customers", "PreferredContact", "TEXT"), ("Customers", "Notes", "TEXT"),
        ("Orders", "DeliveryDate", "TEXT"), ("Orders", "OrderSource", "TEXT"), ("Orders", "Notes", "TEXT")
    };

    public static void Apply(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) connection.Open();
        try
        {
            foreach (var (table, column, sqlType) in Columns)
            {
                if (ColumnExists(connection, table, column)) continue;
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {sqlType} NULL";
                alter.ExecuteNonQuery();
            }
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
    }

    private static bool ColumnExists(System.Data.Common.DbConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
