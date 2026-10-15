using System.Security.Cryptography;
using Excellore.Access.Application;
using Excellore.Access.Infrastructure;
using Excellore.Auth;
using Excellore.Authz;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class InvitationAcceptanceTests
{
    [Fact]
    public async Task Acceptance_requires_verified_matching_identity_and_is_single_use_and_tenant_isolated()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE ROLE access_migrator LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, $"CREATE ROLE access_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE, CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app");
        var migratorString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_migrator", Password = migratorPassword
        }.ConnectionString;
        await using (var migrator = new NpgsqlConnection(migratorString))
        {
            await migrator.OpenAsync();
            foreach (var migration in new[] { "001_initial.sql", "002_register_apps.sql",
                "003_membership_index.sql", "005_audit_append_only.sql",
                "006_custom_role_lifecycle.sql", "008_membership_identity.sql",
                "009_invitation_delivery_state.sql" })
                await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, migration)));
        }
        var appString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var appDb = NpgsqlDataSource.Create(appString);
        var platformActor = Guid.NewGuid();
        Assert.Equal(CatalogSyncResult.Updated,
            await new CatalogSyncService(appDb).SyncAsync("access", new PermissionCatalog("access", [
                new CatalogPermission("access.app.access", "Open Access", false, false, false, ["owner"])
            ]), platformActor));
        var ownerOne = Guid.NewGuid();
        var ownerTwo = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mirror = new IdentityMirrorService(appDb);
        Assert.Equal(IdentityMirrorResult.Created, await mirror.UpsertAsync(
            new VerifiedIdentity(ownerOne, "one@example.invalid", true, null, null)));
        Assert.Equal(IdentityMirrorResult.Created, await mirror.UpsertAsync(
            new VerifiedIdentity(ownerTwo, "two@example.invalid", false, null, null)));
        Assert.Equal(IdentityMirrorResult.Created, await mirror.UpsertAsync(
            new VerifiedIdentity(other, "other@example.invalid", true, null, null)));
        var provisioner = new TenantProvisioningService(appDb);
        var one = (await provisioner.CreateAsync(new("One", "tenant-one", "starter",
            "one@example.invalid", []), platformActor)).Tenant!;
        var two = (await provisioner.CreateAsync(new("Two", "tenant-two", "starter",
            "two@example.invalid", []), platformActor)).Tenant!;
        var oneToken = InvitationToken.Issue(one.TenantId);
        var twoToken = InvitationToken.Issue(two.TenantId);
        await ExecuteAsync(admin, "UPDATE invitations SET token_hash = @hash WHERE id = @id",
            ("hash", oneToken.SecretHash), ("id", one.OwnerInvitationId));
        await ExecuteAsync(admin, "UPDATE invitations SET token_hash = @hash WHERE id = @id",
            ("hash", twoToken.SecretHash), ("id", two.OwnerInvitationId));
        var mySecondInvitations = new InvitationManagementService(appDb, new TenantContextAccessor(),
            new TestUser(ownerTwo), new AccessResolutionService(appDb));
        Assert.Empty(await mySecondInvitations.ListMineAsync());
        Assert.Empty(await new InvitationManagementService(appDb, new TenantContextAccessor(),
            new TestUser(other), new AccessResolutionService(appDb)).ListMineAsync());
        Assert.Equal(InvitationAcceptanceStatus.Unavailable,
            (await new InvitationAcceptanceService(appDb, new TestUser(other), new AccessResolutionService(appDb))
                .AcceptAsync(new(Guid.NewGuid(), InvitationToken.Issue(Guid.NewGuid()).Value))).Status);

        Assert.Equal(InvitationAcceptanceStatus.Unavailable,
            (await new InvitationAcceptanceService(appDb, new TestUser(other), new AccessResolutionService(appDb))
                .AcceptAsync(new(one.OwnerInvitationId, oneToken.Value))).Status);
        Assert.Equal(0L, await CountAsync(admin, "tenant_members"));
        Assert.Equal(InvitationAcceptanceStatus.Unavailable,
            (await new InvitationAcceptanceService(appDb, new TestUser(ownerTwo), new AccessResolutionService(appDb))
                .AcceptAsync(new(two.OwnerInvitationId, twoToken.Value))).Status);
        await mirror.UpsertAsync(new VerifiedIdentity(ownerTwo, "two@example.invalid", true, null, null));
        Assert.Equal(two.TenantId, Assert.Single(await mySecondInvitations.ListMineAsync()).TenantId);
        Assert.Equal(two.TenantId,
            Assert.Single(await mySecondInvitations.ListForUserAsync(ownerTwo)).TenantId);
        Assert.Empty(await mySecondInvitations.ListForUserAsync(other));
        Assert.DoesNotContain(await mySecondInvitations.ListForUserAsync(ownerOne),
            item => item.TenantId == two.TenantId);
        await ExecuteAsync(admin, "UPDATE invitations SET expires_at = now() - interval '1 second' WHERE id = @id",
            ("id", two.OwnerInvitationId));
        Assert.Empty(await mySecondInvitations.ListMineAsync());
        Assert.Equal(InvitationAcceptanceStatus.Unavailable,
            (await new InvitationAcceptanceService(appDb, new TestUser(ownerTwo), new AccessResolutionService(appDb))
                .AcceptAsync(new(two.OwnerInvitationId, twoToken.Value))).Status);

        var accepted = await new InvitationAcceptanceService(appDb, new TestUser(ownerOne), new AccessResolutionService(appDb))
            .AcceptAsync(new(one.OwnerInvitationId, oneToken.Value));
        Assert.Equal(InvitationAcceptanceStatus.Accepted, accepted.Status);
        Assert.Equal(one.TenantId, accepted.TenantId);
        Assert.Equal(InvitationAcceptanceStatus.Unavailable,
            (await new InvitationAcceptanceService(appDb, new TestUser(ownerOne), new AccessResolutionService(appDb))
                .AcceptAsync(new(one.OwnerInvitationId, oneToken.Value))).Status);
        Assert.Equal(1L, await CountAsync(admin,
            "tenant_members WHERE tenant_id = '" + one.TenantId.ToString("D") + "' AND status = 'active'"));
        Assert.Equal(0L, await CountAsync(admin,
            "tenant_members WHERE tenant_id = '" + two.TenantId.ToString("D") + "'"));
        Assert.Equal(1L, await CountAsync(admin,
            "outbox WHERE type = 'tenant.member.added.v1' AND tenant_id = '" + one.TenantId.ToString("D") + "'"));
        Assert.Equal(0L, await CountAsync(admin,
            "outbox WHERE type = 'tenant.member.added.v1' AND payload::text LIKE '%example.invalid%'"));
        Assert.Equal(1L, await CountAsync(admin,
            "role_assignments WHERE tenant_id = '" + one.TenantId.ToString("D") + "'"));
        Assert.Equal(1L, await CountAsync(admin,
            "audit_events WHERE action = 'access.invitation.accepted' AND tenant_id = '" + one.TenantId.ToString("D") + "'"));
    }

    private sealed record TestUser(Guid UserId) : ICurrentUser
    {
        public string? Email => null;
        public string? Acr => "2";
        public bool HasRecentMfa => true;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string where)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {where}", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
