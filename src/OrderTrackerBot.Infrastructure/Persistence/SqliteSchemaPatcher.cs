using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace OrderTrackerBot.Infrastructure.Persistence;

/// <summary>
/// EnsureCreated never alters an existing Sqlite file, so tables and columns added to the model after first deploy
/// would be missing in production. This creates missing tables/indexes and adds missing columns straight from the
/// EF model; it is additive only and safe to run on every start.
/// </summary>
public static class SqliteSchemaPatcher
{
    private static readonly Regex CreateTable = new(@"^CREATE TABLE ""(?<table>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CreateIndex = new(@"^CREATE (?<unique>UNIQUE )?INDEX ", RegexOptions.Compiled);

    // Non-null columns added to an existing table need a constant default for the rows already there.
    private static readonly Dictionary<(string Table, string Column), string> Defaults = new()
    {
        [("Products", "UnitType")] = "'piece'",
        [("Products", "UnitQty")] = "'1.0'",
        [("Orders", "PaymentMethod")] = "1"
    };

    public static void Apply(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) connection.Open();
        try
        {
            var statements = db.Database.GenerateCreateScript()
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var sql in statements)
            {
                var m = CreateTable.Match(sql);
                if (m.Success && !TableExists(connection, m.Groups["table"].Value)) Execute(connection, sql);
            }

            foreach (var entityType in db.Model.GetEntityTypes())
            {
                var table = entityType.GetTableName();
                if (table is null) continue;
                var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
                var existing = ColumnNames(connection, table);

                foreach (var property in entityType.GetProperties())
                {
                    var column = property.GetColumnName(store);
                    if (column is null || existing.Contains(column)) continue;

                    var nullable = property.IsColumnNullable(store);
                    Execute(connection, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {property.GetColumnType()}" +
                        (nullable ? " NULL" : $" NOT NULL DEFAULT {DefaultFor(table, column, property.ClrType)}"));
                }
            }

            foreach (var sql in statements)
            {
                var m = CreateIndex.Match(sql);
                if (m.Success) Execute(connection, CreateIndex.Replace(sql, $"CREATE {m.Groups["unique"].Value}INDEX IF NOT EXISTS ", 1));
            }
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
    }

    private static string DefaultFor(string table, string column, Type clrType)
    {
        if (Defaults.TryGetValue((table, column), out var value)) return value;
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type == typeof(string)) return "''";
        if (type == typeof(DateTime)) return $"'{DateTime.UnixEpoch.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}'";
        if (type == typeof(decimal)) return "'0.0'";
        return "0";
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool TableExists(System.Data.Common.DbConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        var p = command.CreateParameter();
        p.ParameterName = "$name";
        p.Value = table;
        command.Parameters.Add(p);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static HashSet<string> ColumnNames(System.Data.Common.DbConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }
}
