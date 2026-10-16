using System.Text.Json;
using Npgsql;

namespace Excellore.Access.Infrastructure;

public sealed record VerifiedIdentity(Guid UserId, string Email, bool EmailVerified,
    string? GivenName, string? FamilyName);

public enum IdentityMirrorResult { Created, Updated, Unchanged, Conflict, Erased }

public sealed class IdentityMirrorService(NpgsqlDataSource dataSource)
{
    public async Task<IdentityMirrorResult> UpsertAsync(VerifiedIdentity identity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@user::text, 0))", connection, transaction))
        {
            guard.Parameters.AddWithValue("user", identity.UserId);
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var erased = new NpgsqlCommand(
            "SELECT 1 FROM identities WHERE user_id=@user AND status='deleted'", connection, transaction))
        {
            erased.Parameters.AddWithValue("user", identity.UserId);
            if (await erased.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return IdentityMirrorResult.Erased;
            }
        }
        string? oldEmail = null;
        bool oldVerified = false;
        string? oldGiven = null;
        string? oldFamily = null;
        bool exists;
        await using (var select = new NpgsqlCommand("""
            SELECT email, email_verified, given_name, family_name FROM identities
            WHERE user_id = @user FOR UPDATE
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("user", identity.UserId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            exists = await reader.ReadAsync(cancellationToken);
            if (exists)
            {
                oldEmail = reader.GetString(0);
                oldVerified = reader.GetBoolean(1);
                oldGiven = reader.IsDBNull(2) ? null : reader.GetString(2);
                oldFamily = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        var changed = new List<string>();
        if (exists)
        {
            if (!string.Equals(oldEmail, identity.Email, StringComparison.Ordinal)) changed.Add("email");
            if (identity.GivenName is not null && oldGiven != identity.GivenName) changed.Add("givenName");
            if (identity.FamilyName is not null && oldFamily != identity.FamilyName) changed.Add("familyName");
        }
        try
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO identities (user_id, email, email_verified, given_name, family_name, last_seen_at)
                VALUES (@user, @email, @verified, @given, @family, now())
                ON CONFLICT (user_id) DO UPDATE SET
                  email = EXCLUDED.email, email_verified = EXCLUDED.email_verified,
                  given_name = COALESCE(EXCLUDED.given_name, identities.given_name),
                  family_name = COALESCE(EXCLUDED.family_name, identities.family_name),
                  updated_at = CASE WHEN (identities.email, identities.email_verified,
                    identities.given_name, identities.family_name) IS DISTINCT FROM
                    (EXCLUDED.email, EXCLUDED.email_verified,
                     COALESCE(EXCLUDED.given_name, identities.given_name),
                     COALESCE(EXCLUDED.family_name, identities.family_name))
                    THEN now() ELSE identities.updated_at END,
                  last_seen_at = now()
                """, connection, transaction);
            upsert.Parameters.AddWithValue("user", identity.UserId);
            upsert.Parameters.AddWithValue("email", identity.Email);
            upsert.Parameters.AddWithValue("verified", identity.EmailVerified);
            upsert.Parameters.AddWithValue("given", NpgsqlTypes.NpgsqlDbType.Text,
                (object?)identity.GivenName ?? DBNull.Value);
            upsert.Parameters.AddWithValue("family", NpgsqlTypes.NpgsqlDbType.Text,
                (object?)identity.FamilyName ?? DBNull.Value);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return IdentityMirrorResult.Conflict;
        }

        var result = !exists ? IdentityMirrorResult.Created : changed.Count > 0 || oldVerified != identity.EmailVerified
            ? IdentityMirrorResult.Updated : IdentityMirrorResult.Unchanged;
        if (!exists || changed.Count > 0)
        {
            var id = Guid.CreateVersion7();
            var type = exists ? "identity.user.updated.v1" : "identity.user.created.v1";
            var payload = JsonSerializer.Serialize(new
            {
                specversion = "1.0", id, type, source = "excellore/access",
                time = DateTimeOffset.UtcNow, correlationid = Guid.CreateVersion7(),
                actor = new { sub = identity.UserId.ToString("D"), type = "human" },
                data = exists ? (object)new { userId = identity.UserId, changed } : new { userId = identity.UserId }
            });
            await using var publish = new NpgsqlCommand("""
                INSERT INTO outbox (id, type, tenant_id, payload)
                VALUES (@id, @type, NULL, @payload::jsonb)
                """, connection, transaction);
            publish.Parameters.AddWithValue("id", id);
            publish.Parameters.AddWithValue("type", type);
            publish.Parameters.AddWithValue("payload", payload);
            await publish.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
