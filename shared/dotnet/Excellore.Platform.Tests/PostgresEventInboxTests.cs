using System.Data.Common;
using System.Security.Cryptography;
using Excellore.Platform;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Platform.Tests;

public sealed class PostgresEventInboxTests
{
    [Fact]
    public async Task Inbox_marker_and_tenant_work_commit_once_or_roll_back_together()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE ROLE inbox_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS;");
        await ExecuteAsync(admin, """
            CREATE TABLE inbox (
                event_id uuid PRIMARY KEY,
                received_at timestamptz NOT NULL DEFAULT now()
            );
            COMMENT ON TABLE inbox IS 'Class P: consumer event IDs';
            CREATE TABLE tenant_items (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL
            );
            COMMENT ON TABLE tenant_items IS 'Class T: inbox isolation fixture';
            ALTER TABLE tenant_items ENABLE ROW LEVEL SECURITY;
            ALTER TABLE tenant_items FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_items_isolation ON tenant_items
              USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
              WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
            GRANT USAGE ON SCHEMA public TO inbox_app;
            GRANT SELECT, INSERT ON inbox, tenant_items TO inbox_app;
            """);

        var appConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "inbox_app",
            Password = appPassword
        }.ConnectionString;
        await using var app = new NpgsqlConnection(appConnectionString);
        await app.OpenAsync();
        var inbox = new PostgresEventInbox();
        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        var firstEvent = Guid.NewGuid();
        var firstItem = Guid.NewGuid();

        Assert.True(await inbox.RunOnceAsync(app, firstEvent, tenantOne,
            (transaction, cancellationToken) => InsertItemAsync(transaction, firstItem,
                tenantOne, cancellationToken)));
        Assert.False(await inbox.RunOnceAsync(app, firstEvent, tenantOne,
            (_, _) => throw new InvalidOperationException("Duplicate work ran.")));
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        var failedEvent = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.RunOnceAsync(
            app, failedEvent, tenantOne, async (transaction, cancellationToken) =>
            {
                await InsertItemAsync(transaction, Guid.NewGuid(), tenantOne, cancellationToken);
                throw new InvalidOperationException("Synthetic handler failure.");
            }));
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        Assert.True(await inbox.RunOnceAsync(app, failedEvent, tenantOne,
            (transaction, cancellationToken) => InsertItemAsync(transaction, Guid.NewGuid(),
                tenantOne, cancellationToken)));
        Assert.Equal(2, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(2, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        var wrongTenantEvent = Guid.NewGuid();
        await Assert.ThrowsAsync<PostgresException>(() => inbox.RunOnceAsync(
            app, wrongTenantEvent, tenantOne, async (transaction, cancellationToken) =>
            {
                Assert.Equal(0, await CountTenantItemsAsync(transaction, tenantTwo,
                    cancellationToken));
                await InsertItemAsync(transaction, Guid.NewGuid(), tenantTwo, cancellationToken);
            }));
        Assert.Equal(2, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(2, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        Assert.True(await inbox.RunOnceAsync(app, Guid.NewGuid(), null,
            async (transaction, cancellationToken) =>
                Assert.Equal(0, await CountTenantItemsAsync(transaction, tenantOne,
                    cancellationToken))));
        Assert.Equal(3, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(2, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));
    }

    private static async Task InsertItemAsync(DbTransaction transaction, Guid id, Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO tenant_items (id, tenant_id) VALUES (@id, @tenant_id)";
        AddGuid(command, "id", id);
        AddGuid(command, "tenant_id", tenantId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CountTenantItemsAsync(DbTransaction transaction, Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM tenant_items WHERE tenant_id = @tenant_id";
        AddGuid(command, "tenant_id", tenantId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static void AddGuid(DbCommand command, string name, Guid value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
