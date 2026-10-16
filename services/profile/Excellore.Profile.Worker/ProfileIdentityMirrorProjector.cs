using Excellore.Authz;
using Excellore.Contracts;
using Excellore.Platform;
using Npgsql;

namespace Excellore.Profile.Worker;

/// <summary>Refreshes existing global profile mirrors from Keycloak after an ID-only event.</summary>
public sealed class ProfileIdentityMirrorProjector(ProfileWorkerDatabase database,
    PostgresEventInbox inbox, IKeycloakDirectory directory)
{
    public async Task<bool> ApplyAsync(ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var envelope = IdentityUserUpdatedV1.Parse(body.Span);
        var userId = envelope.Data!.UserId;
        var changed = envelope.Data.Changed!;
        KeycloakDirectoryUser? user = null;
        if (changed.Any(field => field is "givenName" or "familyName" or "locale"))
        {
            var users = await directory.ListAsync(cancellationToken);
            if (!users.TryGetValue(userId, out user))
                throw new InvalidOperationException("Identity mirror source unavailable.");
        }

        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        return await inbox.RunOnceAsync(connection, envelope.Id, null,
            async (transaction, token) =>
            {
                if (user is null) return;
                await using var update = new NpgsqlCommand("""
                    UPDATE user_profiles SET
                      mirror_first_name=@first,mirror_last_name=@last,
                      mirror_locale=@locale,updated_at=now()
                    WHERE user_id=@user AND deleted_at IS NULL
                    """, connection, (NpgsqlTransaction)transaction);
                update.Parameters.AddWithValue("user", userId);
                update.Parameters.AddWithValue("first", (object?)user.FirstName ?? DBNull.Value);
                update.Parameters.AddWithValue("last", (object?)user.LastName ?? DBNull.Value);
                update.Parameters.AddWithValue("locale", (object?)user.Locale ?? DBNull.Value);
                await update.ExecuteNonQueryAsync(token);
            }, cancellationToken);
    }
}
