using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Excellore.Contracts;
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
            CREATE TABLE global_event_effects (event_id uuid PRIMARY KEY);
            COMMENT ON TABLE global_event_effects IS 'Class P: global identity consumer fixture';
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
            GRANT SELECT, INSERT ON inbox, tenant_items, global_event_effects TO inbox_app;
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

        var suspension = new PlatformEvent<TenantSuspendedV1>
        {
            SpecVersion = "1.0",
            Id = Guid.NewGuid(),
            Type = TenantSuspendedV1.EventType,
            Source = TenantSuspendedV1.EventSource,
            Time = DateTimeOffset.UtcNow,
            TenantId = tenantOne,
            CorrelationId = Guid.NewGuid(),
            Actor = new EventActor { Sub = Guid.NewGuid().ToString("D"), Type = "machine" },
            Data = new TenantSuspendedV1 { TenantId = tenantOne, ReasonCode = "platform_security" }
        };
        var suspensionJson = JsonSerializer.SerializeToUtf8Bytes(suspension);
        var suspensionItem = Guid.NewGuid();
        async Task<bool> ConsumeSuspensionAsync()
        {
            var parsed = TenantSuspendedV1.Parse(suspensionJson);
            return await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (transaction, cancellationToken) => InsertItemAsync(transaction, suspensionItem,
                    parsed.TenantId!.Value, cancellationToken));
        }

        Assert.True(await ConsumeSuspensionAsync());
        Assert.False(await ConsumeSuspensionAsync());
        Assert.Equal(4, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(3, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        var identityTypes = new[]
        {
            IdentityUserCreatedV1.EventType,
            IdentityUserUpdatedV1.EventType,
            IdentityUserDisabledV1.EventType,
            IdentityUserEnabledV1.EventType,
            IdentityUserDeletedV1.EventType
        };
        foreach (var type in identityTypes)
        {
            var data = new Dictionary<string, object> { ["userId"] = Guid.NewGuid() };
            if (type == IdentityUserUpdatedV1.EventType) data["changed"] = new[] { "locale" };
            var json = IdentityUserEventsV1Tests.EventJson(type, data);
            var eventId = type switch
            {
                IdentityUserCreatedV1.EventType => IdentityUserCreatedV1.Parse(json).Id,
                IdentityUserUpdatedV1.EventType => IdentityUserUpdatedV1.Parse(json).Id,
                IdentityUserDisabledV1.EventType => IdentityUserDisabledV1.Parse(json).Id,
                IdentityUserEnabledV1.EventType => IdentityUserEnabledV1.Parse(json).Id,
                IdentityUserDeletedV1.EventType => IdentityUserDeletedV1.Parse(json).Id,
                _ => throw new InvalidOperationException("Unexpected identity event type.")
            };
            Assert.True(await inbox.RunOnceAsync(app, eventId, null,
                (transaction, cancellationToken) => InsertGlobalEffectAsync(transaction, eventId,
                    cancellationToken)));
            Assert.False(await inbox.RunOnceAsync(app, eventId, null,
                (_, _) => throw new InvalidOperationException("Duplicate global work ran.")));
        }
        Assert.Equal(9, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(5, await CountAsync(admin, "SELECT count(*) FROM global_event_effects"));
        Assert.Equal(3, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        foreach (var type in new[]
        {
            TenantReactivatedV1.EventType,
            TenantDeletionRequestedV1.EventType,
            TenantDeletedV1.EventType
        })
        {
            var json = TenantLifecycleEventsV1Tests.EventJson(type, tenantOne,
                TenantLifecycleEventsV1Tests.Data(type, tenantOne));
            var parsed = TenantLifecycleEventsV1Tests.Parse(type, json);
            Assert.True(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (transaction, cancellationToken) => InsertItemAsync(transaction, parsed.Id,
                    parsed.TenantId, cancellationToken)));
            Assert.False(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (_, _) => throw new InvalidOperationException("Duplicate tenant work ran.")));
        }
        Assert.Equal(12, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(6, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        foreach (var type in new[]
        {
            TenantCreatedV1.EventType,
            TenantUpdatedV1.EventType,
            TenantAppEnabledV1.EventType,
            TenantAppDisabledV1.EventType
        })
        {
            var json = TenantLifecycleEventsV1Tests.EventJson(type, tenantOne,
                TenantMetadataEventsV1Tests.Data(type, tenantOne));
            var parsed = TenantMetadataEventsV1Tests.Parse(type, json);
            Assert.True(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (transaction, cancellationToken) => InsertItemAsync(transaction, parsed.Id,
                    parsed.TenantId, cancellationToken)));
            Assert.False(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (_, _) => throw new InvalidOperationException("Duplicate metadata work ran.")));
        }
        Assert.Equal(16, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(10, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        foreach (var type in new[]
        {
            TenantMemberAddedV1.EventType,
            TenantMemberSuspendedV1.EventType,
            TenantMemberReactivatedV1.EventType,
            TenantMemberRemovedV1.EventType
        })
        {
            var json = TenantLifecycleEventsV1Tests.EventJson(type, tenantOne,
                TenantMemberEventsV1Tests.Data(type, tenantOne));
            var parsed = TenantMemberEventsV1Tests.Parse(type, json);
            Assert.True(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (transaction, cancellationToken) => InsertItemAsync(transaction, parsed.Id,
                    parsed.TenantId, cancellationToken)));
            Assert.False(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (_, _) => throw new InvalidOperationException("Duplicate member work ran.")));
        }
        Assert.Equal(20, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(14, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        foreach (var type in new[]
        {
            AccessRoleCreatedV1.EventType,
            AccessRoleUpdatedV1.EventType,
            AccessRoleDeletedV1.EventType
        })
        {
            var json = TenantLifecycleEventsV1Tests.EventJson(type, tenantOne,
                AccessRoleEventsV1Tests.Data(tenantOne));
            var parsed = AccessRoleEventsV1Tests.Parse(type, json);
            Assert.True(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (transaction, cancellationToken) => InsertItemAsync(transaction, parsed.Id,
                    parsed.TenantId, cancellationToken)));
            Assert.False(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (_, _) => throw new InvalidOperationException("Duplicate role work ran.")));
        }
        Assert.Equal(23, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(17, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        foreach (var type in new[]
        {
            AccessAssignmentGrantedV1.EventType,
            AccessAssignmentRevokedV1.EventType
        })
        {
            var json = TenantLifecycleEventsV1Tests.EventJson(type, tenantOne,
                AccessAssignmentEventsV1Tests.Data(tenantOne));
            var parsed = AccessAssignmentEventsV1Tests.Parse(type, json);
            Assert.True(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (transaction, cancellationToken) => InsertItemAsync(transaction, parsed.Id,
                    parsed.TenantId, cancellationToken)));
            Assert.False(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (_, _) => throw new InvalidOperationException("Duplicate assignment work ran.")));
        }
        Assert.Equal(25, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(19, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));

        foreach (var type in new[]
        {
            AccessSupportGrantCreatedV1.EventType,
            AccessSupportGrantRevokedV1.EventType
        })
        {
            var json = TenantLifecycleEventsV1Tests.EventJson(type, tenantOne,
                AccessSupportGrantEventsV1Tests.Data(tenantOne));
            var parsed = AccessSupportGrantEventsV1Tests.Parse(type, json);
            Assert.True(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (transaction, cancellationToken) => InsertItemAsync(transaction, parsed.Id,
                    parsed.TenantId, cancellationToken)));
            Assert.False(await inbox.RunOnceAsync(app, parsed.Id, parsed.TenantId,
                (_, _) => throw new InvalidOperationException("Duplicate support grant work ran.")));
        }
        Assert.Equal(27, await CountAsync(admin, "SELECT count(*) FROM inbox"));
        Assert.Equal(21, await CountAsync(admin, "SELECT count(*) FROM tenant_items"));
    }

    private static async Task InsertGlobalEffectAsync(DbTransaction transaction, Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO global_event_effects (event_id) VALUES (@event_id)";
        AddGuid(command, "event_id", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
