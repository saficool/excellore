using System.Security.Cryptography;
using Excellore.Platform.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Sample.Tests;

public sealed class SampleIsolationTests
{
    [Fact]
    public async Task GeneratedMigrationEnforcesTenantIsolationForRestrictedRole()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var publisherPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();

        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await Exec(admin, "CREATE ROLE \"__APP_SLUG___app\" LOGIN PASSWORD '" + appPassword + "' NOSUPERUSER NOBYPASSRLS;");
        await Exec(admin, "CREATE ROLE \"__APP_SLUG___publisher\" LOGIN PASSWORD '" + publisherPassword + "' NOSUPERUSER NOBYPASSRLS;");
        await Exec(admin, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "001_initial.sql")));

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        await Exec(admin, "INSERT INTO sample_items (id, tenant_id, code) VALUES (@id, @tenant, 'ONE');",
            ("id", id1), ("tenant", t1));
        await Exec(admin, "INSERT INTO sample_items (id, tenant_id, code) VALUES (@id, @tenant, 'TWO');",
            ("id", id2), ("tenant", t2));

        var appConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "__APP_SLUG___app", Password = appPassword
        }.ConnectionString;
        await using var app = new NpgsqlConnection(appConnectionString);
        await app.OpenAsync();
        Assert.True((await RlsDatabaseChecks.GetRuntimeRoleStatusAsync(app)).IsSafe);
        var readOutbox = await Assert.ThrowsAsync<PostgresException>(() =>
            Scalar<long>(app, "SELECT count(*) FROM outbox"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, readOutbox.SqlState);
        var publisherConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "__APP_SLUG___publisher", Password = publisherPassword
        }.ConnectionString;
        await using var publisher = new NpgsqlConnection(publisherConnectionString);
        await publisher.OpenAsync();
        Assert.True((await RlsDatabaseChecks.GetRuntimeRoleStatusAsync(publisher)).IsSafe);
        Assert.Equal(0L, await Scalar<long>(publisher, "SELECT count(*) FROM outbox"));
        var readItems = await Assert.ThrowsAsync<PostgresException>(() =>
            Scalar<long>(publisher, "SELECT count(*) FROM sample_items"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, readItems.SqlState);
        var exemptions = RlsDatabaseChecks.ParseExemptions(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "rls-exempt.txt")));
        Assert.Empty(await RlsDatabaseChecks.FindUnprotectedTenantTablesAsync(app, exemptions));
        Assert.Equal(0L, await Scalar<long>(app, "SELECT count(*) FROM sample_items"));

        await using (var tx = await app.BeginTransactionAsync())
        {
            await Scalar<string>(app, "SELECT set_config('app.tenant_id', @tenant, true)",
                ("tenant", t1.ToString("D")));
            Assert.Equal(id1, await Scalar<Guid>(app, "SELECT id FROM sample_items"));
            Assert.Equal(0, await Exec(app, "UPDATE sample_items SET code = 'WRONG' WHERE id = @id",
                ("id", id2)));
            var error = await Assert.ThrowsAsync<PostgresException>(() => Exec(app,
                "INSERT INTO sample_items (id, tenant_id, code) VALUES (@id, @tenant, 'WRONG')",
                ("id", Guid.NewGuid()), ("tenant", t2)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            await tx.RollbackAsync();
        }

        Assert.Equal(0L, await Scalar<long>(app, "SELECT count(*) FROM sample_items"));
        await using (var tx = await app.BeginTransactionAsync())
        {
            await Scalar<string>(app, "SELECT set_config('app.tenant_id', @tenant, true)",
                ("tenant", t2.ToString("D")));
            Assert.Equal(id2, await Scalar<Guid>(app, "SELECT id FROM sample_items"));
            await tx.CommitAsync();
        }
        Assert.Equal(0L, await Scalar<long>(app, "SELECT count(*) FROM sample_items"));
    }

    private static async Task<int> Exec(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected database value."));
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql,
        (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
