using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record NotificationContact(string Email);

public sealed class NotificationContactService(NpgsqlDataSource dataSource)
{
    public async Task<NotificationContact?> ResolveAsync(Guid tenantId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await context.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("""
            SELECT i.email
              FROM tenant_members m
              JOIN identities i ON i.user_id=m.user_id
              JOIN tenants t ON t.id=m.tenant_id
             WHERE m.tenant_id=@tenant AND m.user_id=@user
               AND m.status='active' AND t.status='active'
               AND i.status='active' AND i.email_verified
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        var email = await command.ExecuteScalarAsync(cancellationToken) as string;
        await transaction.CommitAsync(cancellationToken);
        return email is null ? null : new NotificationContact(email);
    }
}
