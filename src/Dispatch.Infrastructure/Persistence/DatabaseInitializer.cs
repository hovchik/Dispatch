using System.Data;
using Dispatch.Application.Abstractions;
using Dispatch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Dispatch.Infrastructure.Persistence;

public sealed class DatabaseInitializer(IDbContextFactory<DispatchDbContext> factory)
{
    private const string SeededKey = "seeded";

    /// <summary>Creates the schema on first run and adds a small "Getting started" collection.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Database.EnsureCreatedAsync(ct))
            await UpgradeSchemaAsync(db, ct);

        if (await db.Settings.AnyAsync(s => s.Key == SeededKey, ct))
            return;

        var environment = new ApiEnvironment
        {
            Name = "httpbin",
            Variables =
            [
                new("baseUrl", "https://httpbin.org"),
                new("token", "my-secret-token")
            ]
        };
        db.Environments.Add(environment);
        db.Settings.Add(new SettingEntry { Key = SettingKeys.ActiveEnvironmentId, Value = environment.Id.ToString() });

        db.Collections.Add(new RequestCollection
        {
            Name = "Getting Started",
            Requests =
            [
                new ApiRequest
                {
                    Name = "Get with query params",
                    Method = HttpVerb.Get,
                    Url = "{{baseUrl}}/get?page=1&size=20",
                    QueryParams = [new("page", "1"), new("size", "20")],
                    SortOrder = 0
                },
                new ApiRequest
                {
                    Name = "Create (JSON body)",
                    Method = HttpVerb.Post,
                    Url = "{{baseUrl}}/post",
                    Body = new RequestBody
                    {
                        Mode = BodyMode.Json,
                        Content = """
                                  {
                                    "name": "Dispatch",
                                    "active": true,
                                    "tags": ["api", "client"]
                                  }
                                  """
                    },
                    SortOrder = 1
                },
                new ApiRequest
                {
                    Name = "Bearer auth",
                    Method = HttpVerb.Get,
                    Url = "{{baseUrl}}/bearer",
                    Auth = new AuthSettings { Mode = AuthMode.Bearer, Token = "{{token}}" },
                    SortOrder = 2
                }
            ]
        });

        db.Settings.Add(new SettingEntry { Key = SeededKey, Value = "1" });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds columns that newer versions of the model introduced to a database created by an older version.
    /// (EnsureCreated only creates missing databases; it never alters existing tables.)
    /// </summary>
    private static async Task UpgradeSchemaAsync(DispatchDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null)
                continue;

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info(\"{table}\")";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    existing.Add(reader.GetString(1));
            }
            if (existing.Count == 0)
            {
                // A whole table added by a newer version (e.g. Flows, Monitors). Create it, then continue.
                await CreateTableAsync(connection, entity, table, ct);
                continue;
            }

            var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(storeObject);
                if (column is null || existing.Contains(column))
                    continue;

                var type = property.GetColumnType();
                var definition = property.IsNullable
                    ? $"\"{column}\" {type} NULL"
                    : $"\"{column}\" {type} NOT NULL DEFAULT {DefaultLiteral(property)}";
                // A plain command: the JSON default literal contains braces, which ExecuteSqlRaw treats as placeholders.
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN {definition}";
                await alter.ExecuteNonQueryAsync(ct);
            }
        }
    }

    /// <summary>Creates a table for an entity type added by a newer version, using its mapped columns and primary key.</summary>
    private static async Task CreateTableAsync(System.Data.Common.DbConnection connection, IEntityType entity, string table, CancellationToken ct)
    {
        var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
        var columns = new List<string>();
        foreach (var property in entity.GetProperties())
        {
            var column = property.GetColumnName(storeObject);
            if (column is null)
                continue;
            var nullability = property.IsNullable ? "NULL" : "NOT NULL";
            columns.Add($"\"{column}\" {property.GetColumnType()} {nullability}");
        }
        var key = entity.FindPrimaryKey();
        if (key is not null)
            columns.Add($"PRIMARY KEY ({string.Join(", ", key.Properties.Select(p => $"\"{p.GetColumnName(storeObject)}\""))})");

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE IF NOT EXISTS \"{table}\" ({string.Join(", ", columns)})";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string DefaultLiteral(IProperty property)
    {
        var clr = property.ClrType;
        if (property.GetValueConverter() is { } converter && converter.ProviderClrType == typeof(string))
        {
            if (clr.IsEnum)
                return $"'{Enum.GetNames(clr)[0]}'";
            if (clr != typeof(string))
            {
                // JSON column: an empty list or a default-constructed object.
                var json = typeof(System.Collections.IEnumerable).IsAssignableFrom(clr)
                    ? "[]"
                    : JsonColumn.Serialize(Activator.CreateInstance(clr));
                return $"'{json.Replace("'", "''")}'";
            }
        }
        return clr == typeof(string) ? "''" : "0";
    }
}
