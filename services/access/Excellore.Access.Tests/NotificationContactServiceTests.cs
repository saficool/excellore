using System.Security.Cryptography;
using Excellore.Access.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Excellore.Access.Tests;

public sealed class NotificationContactServiceTests
{
    [Fact]
    public async Task Resolver_returns_only_verified_active_contact_in_the_requested_tenant()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin,
            $"CREATE ROLE access_migrator LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin,
            $"CREATE ROLE access_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS");
        await ExecuteAsync(admin, "GRANT USAGE, CREATE ON SCHEMA public TO access_migrator");
        await ExecuteAsync(admin, "GRANT USAGE ON SCHEMA public TO access_app");
        await ExecuteAsync(admin,
            "ALTER DEFAULT PRIVILEGES FOR ROLE access_migrator IN SCHEMA public GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO access_app");

        var migratorString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_migrator", Password = migratorPassword
        }.ConnectionString;
        await using (var migrator = new NpgsqlConnection(migratorString))
        {
            await migrator.OpenAsync();
            await ExecuteAsync(migrator, await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "001_initial.sql")));
        }

        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        var member = Guid.NewGuid();
        var unverified = Guid.NewGuid();
        await ExecuteAsync(admin, """
            INSERT INTO tenants(id,slug,name,status) VALUES
              (@tenantOne,'tenant-one','Tenant One','active'),
              (@tenantTwo,'tenant-two','Tenant Two','active');
            INSERT INTO identities(user_id,email,email_verified,status) VALUES
              (@member,'member@example.test',true,'active'),
              (@unverified,'unverified@example.test',false,'active');
            INSERT INTO tenant_members(tenant_id,user_id,status) VALUES
              (@tenantOne,@member,'active'),(@tenantOne,@unverified,'active');
            """, ("tenantOne", tenantOne), ("tenantTwo", tenantTwo),
            ("member", member), ("unverified", unverified));

        var appString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "access_app", Password = appPassword
        }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(appString);
        var service = new NotificationContactService(dataSource);

        Assert.Equal("member@example.test",
            (await service.ResolveAsync(tenantOne, member))?.Email);
        Assert.Null(await service.ResolveAsync(tenantTwo, member));
        Assert.Null(await service.ResolveAsync(tenantOne, unverified));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
}
