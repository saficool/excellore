using System.Security.Cryptography;
using Excellore.Platform.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Platform.Tests;

public sealed class RlsDatabaseChecksTests
{
    [Fact]
    public void ExemptionFileParserIgnoresCommentsAndBlankLines()
    {
        Assert.Equal(["outbox", "support_grants"],
            RlsDatabaseChecks.ParseExemptions("# reviewed\r\noutbox\n\n support_grants \n"));
    }

    [Fact]
    public async Task RestrictedRoleAndForcedRlsIsolateTenantRows()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword)
            .Build();
        await container.StartAsync();

        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        // Both passwords are random hexadecimal generated for this disposable database only.
        await ExecuteAsync(admin, $"CREATE ROLE rls_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS;");
        await ExecuteAsync(admin, """
            CREATE TABLE tenant_items (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                value text NOT NULL
            );
            COMMENT ON TABLE tenant_items IS 'Class T: tenant-owned RLS test fixture';
            GRANT USAGE ON SCHEMA public TO rls_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_items TO rls_app;
            """);

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var t1Item = Guid.NewGuid();
        var t2Item = Guid.NewGuid();
        await ExecuteAsync(admin, "INSERT INTO tenant_items VALUES (@id, @tenant, 't1');", ("id", t1Item), ("tenant", t1));
        await ExecuteAsync(admin, "INSERT INTO tenant_items VALUES (@id, @tenant, 't2');", ("id", t2Item), ("tenant", t2));

        var appConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "rls_app",
            Password = appPassword
        }.ConnectionString;
        await using var app = new NpgsqlConnection(appConnectionString);
        await app.OpenAsync();
        var exemptions = RlsDatabaseChecks.ParseExemptions(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "rls-exempt.txt")));

        Assert.True((await RlsDatabaseChecks.GetRuntimeRoleStatusAsync(app)).IsSafe);
        await ExecuteAsync(admin, """
            CREATE ROLE rls_owner NOLOGIN;
            CREATE TABLE ownership_probe (id integer);
            ALTER TABLE ownership_probe OWNER TO rls_owner;
            GRANT rls_owner TO rls_app;
            """);
        Assert.True((await RlsDatabaseChecks.GetRuntimeRoleStatusAsync(app)).OwnsPublicTable);
        await ExecuteAsync(admin, """
            REVOKE rls_owner FROM rls_app;
            DROP TABLE ownership_probe;
            DROP ROLE rls_owner;
            """);
        Assert.True((await RlsDatabaseChecks.GetRuntimeRoleStatusAsync(app)).IsSafe);
        Assert.Equal(["tenant_items"], await RlsDatabaseChecks.FindUnprotectedTenantTablesAsync(app, exemptions));

        await ExecuteAsync(admin, """
            ALTER TABLE tenant_items ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON tenant_items
              USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
              WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            """);
        Assert.Equal(["tenant_items"], await RlsDatabaseChecks.FindUnprotectedTenantTablesAsync(app, exemptions));
        await ExecuteAsync(admin, "ALTER TABLE tenant_items FORCE ROW LEVEL SECURITY;");
        Assert.Empty(await RlsDatabaseChecks.FindUnprotectedTenantTablesAsync(app, exemptions));

        Assert.Equal(0L, await CountAsync(app));
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, t1);
            Assert.Equal(1L, await CountAsync(app));
            Assert.Equal(t1Item, await ScalarAsync<Guid>(app, "SELECT id FROM tenant_items;"));
            await transaction.CommitAsync();
        }
        Assert.Equal(0L, await CountAsync(app));

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, t2);
            Assert.Equal(1L, await CountAsync(app));
            Assert.Equal(t2Item, await ScalarAsync<Guid>(app, "SELECT id FROM tenant_items;"));

            var hiddenUpdate = await ExecuteAsync(app,
                "UPDATE tenant_items SET value = 'wrong' WHERE id = @id;", ("id", t1Item));
            Assert.Equal(0, hiddenUpdate);

            var insertError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app,
                "INSERT INTO tenant_items VALUES (@id, @tenant, 'wrong');",
                ("id", Guid.NewGuid()), ("tenant", t1)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insertError.SqlState);
            await transaction.RollbackAsync();
        }

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(app, t2);
            var updateError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app,
                "UPDATE tenant_items SET tenant_id = @tenant WHERE id = @id;",
                ("tenant", t1), ("id", t2Item)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateError.SqlState);
            await transaction.RollbackAsync();
        }

        Assert.Equal(0L, await CountAsync(app));
        Assert.Equal("t1", await ScalarAsync<string>(admin,
            "SELECT value FROM tenant_items WHERE id = @id;", ("id", t1Item)));
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId) =>
        await ScalarAsync<string>(connection, "SELECT set_config('app.tenant_id', @tenant, true);",
            ("tenant", tenantId.ToString()));

    private static Task<long> CountAsync(NpgsqlConnection connection) =>
        ScalarAsync<long>(connection, "SELECT count(*) FROM tenant_items;");

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected a database value."));
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }
}
