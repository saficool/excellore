using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Excellore.Platform;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Platform.Tests;

public sealed class PostgresEventOutboxTests
{
    [Fact]
    public async Task Outbox_event_commits_with_tenant_work_and_rolls_back_with_it()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE ROLE outbox_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS;");
        await ExecuteAsync(admin, """
            CREATE TABLE outbox (
                id uuid PRIMARY KEY,
                type text NOT NULL,
                tenant_id uuid,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                published_at timestamptz
            );
            COMMENT ON TABLE outbox IS 'Class P, RLS-exempt: routing metadata';
            CREATE TABLE tenant_items (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL
            );
            COMMENT ON TABLE tenant_items IS 'Class T: outbox isolation fixture';
            ALTER TABLE tenant_items ENABLE ROW LEVEL SECURITY;
            ALTER TABLE tenant_items FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_items_isolation ON tenant_items
              USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
              WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
            GRANT USAGE ON SCHEMA public TO outbox_app;
            GRANT INSERT ON outbox TO outbox_app;
            GRANT INSERT ON tenant_items TO outbox_app;
            """);

        var appConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "outbox_app",
            Password = appPassword
        }.ConnectionString;
        await using var app = new NpgsqlConnection(appConnectionString);
        await app.OpenAsync();
        var outbox = new PostgresEventOutbox();
        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        const string eventType = "profile.updated.v1";
        var committedEvent = Guid.NewGuid();
        var committedPayload = Payload(committedEvent, eventType, tenantOne);

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(transaction, tenantOne);
            await InsertItemAsync(transaction, Guid.NewGuid(), tenantOne);
            await outbox.EnqueueAsync(transaction, committedEvent, eventType, tenantOne,
                committedPayload);
            await transaction.CommitAsync();
        }
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM outbox"));
        await using (var command = new NpgsqlCommand(
                         "SELECT type, tenant_id, payload->>'id' FROM outbox WHERE id = @id", admin))
        {
            command.Parameters.AddWithValue("id", committedEvent);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(eventType, reader.GetString(0));
            Assert.Equal(tenantOne, reader.GetGuid(1));
            Assert.Equal(committedEvent.ToString("D"), reader.GetString(2));
        }

        var rolledBackEvent = Guid.NewGuid();
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(transaction, tenantOne);
            await InsertItemAsync(transaction, Guid.NewGuid(), tenantOne);
            await outbox.EnqueueAsync(transaction, rolledBackEvent, eventType, tenantOne,
                Payload(rolledBackEvent, eventType, tenantOne));
            await transaction.RollbackAsync();
        }
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM outbox"));

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<PostgresException>(() => outbox.EnqueueAsync(transaction,
                committedEvent, eventType, tenantOne, committedPayload));
            await transaction.RollbackAsync();
        }
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM outbox"));

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SetTenantAsync(transaction, tenantOne);
            await Assert.ThrowsAsync<ArgumentException>(() => outbox.EnqueueAsync(transaction,
                Guid.NewGuid(), eventType, tenantOne,
                Payload(Guid.NewGuid(), eventType, tenantTwo)));
            var malformed = Encoding.UTF8.GetBytes("{\"privateMarker\":\"test-value\",invalid}");
            var error = await Assert.ThrowsAsync<ArgumentException>(() => outbox.EnqueueAsync(
                transaction, Guid.NewGuid(), eventType, tenantOne, malformed));
            Assert.DoesNotContain("test-value", error.ToString());
            await Assert.ThrowsAsync<PostgresException>(() => InsertItemAsync(transaction,
                Guid.NewGuid(), tenantTwo));
            await transaction.RollbackAsync();
        }
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));
        Assert.Equal(1, await CountAsync(admin, "SELECT count(*) FROM outbox"));

        var globalEvent = Guid.NewGuid();
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await outbox.EnqueueAsync(transaction, globalEvent, "identity.user.disabled.v1", null,
                Payload(globalEvent, "identity.user.disabled.v1", null));
            await transaction.CommitAsync();
        }
        await using (var command = new NpgsqlCommand(
                         "SELECT count(*) FROM outbox WHERE id = @id AND tenant_id IS NULL", admin))
        {
            command.Parameters.AddWithValue("id", globalEvent);
            Assert.Equal(1, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    private static byte[] Payload(Guid eventId, string eventType, Guid? tenantId) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0",
            id = eventId,
            type = eventType,
            tenantid = tenantId
        });

    private static async Task SetTenantAsync(DbTransaction transaction, Guid tenantId)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT set_config('app.tenant_id', @tenant_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.Value = tenantId.ToString("D");
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertItemAsync(DbTransaction transaction, Guid id, Guid tenantId)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO tenant_items (id, tenant_id) VALUES (@id, @tenant_id)";
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "id";
        idParameter.Value = id;
        command.Parameters.Add(idParameter);
        var tenantParameter = command.CreateParameter();
        tenantParameter.ParameterName = "tenant_id";
        tenantParameter.Value = tenantId;
        command.Parameters.Add(tenantParameter);
        await command.ExecuteNonQueryAsync();
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
