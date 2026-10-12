using Npgsql;

namespace Excellore.Platform.Testing;

/// <summary>Database checks that service isolation suites run as their restricted runtime role.</summary>
public static class RlsDatabaseChecks
{
    public static string[] ParseExemptions(string contents) =>
        contents.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();

    public static async Task<IReadOnlyList<string>> FindUnprotectedTenantTablesAsync(
        NpgsqlConnection connection, IReadOnlyCollection<string> exemptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(exemptions);

        const string sql = """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN information_schema.columns col
              ON col.table_schema = n.nspname AND col.table_name = c.relname
              AND col.column_name = 'tenant_id'
            WHERE n.nspname = 'public' AND c.relkind = 'r'
              AND c.relname <> ALL (@exempt)
              AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity)
            ORDER BY c.relname;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("exempt", exemptions.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public static async Task<RuntimeRoleStatus> GetRuntimeRoleStatusAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        const string sql = """
            SELECT r.rolsuper, r.rolbypassrls,
              EXISTS (
                SELECT 1 FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
                  AND pg_has_role(r.oid, c.relowner, 'USAGE')
              )
            FROM pg_roles r WHERE r.rolname = current_user;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Database runtime role was not found.");
        }

        return new RuntimeRoleStatus(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }
}

public sealed record RuntimeRoleStatus(bool IsSuperuser, bool BypassesRls, bool OwnsPublicTable)
{
    public bool IsSafe => !IsSuperuser && !BypassesRls && !OwnsPublicTable;
}
