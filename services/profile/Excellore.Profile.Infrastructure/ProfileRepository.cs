using System.Text.Json;
using Excellore.Profile.Application;
using Npgsql;

namespace Excellore.Profile.Infrastructure;

public sealed class ProfileRepository(NpgsqlDataSource db)
{
    public async Task EnsureMirrorAsync(Guid userId, string? firstName, string? lastName,
        string? locale, CancellationToken ct = default)
    {
        static string? Bounded(string? value, int length) =>
            value is { Length: > 0 } && value.Length <= length &&
            !value.Any(char.IsControl) ? value : null;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO user_profiles(user_id,mirror_first_name,mirror_last_name,mirror_locale)
            VALUES (@user,@first,@last,@locale)
            ON CONFLICT(user_id) DO UPDATE SET
              mirror_first_name=COALESCE(user_profiles.mirror_first_name,excluded.mirror_first_name),
              mirror_last_name=COALESCE(user_profiles.mirror_last_name,excluded.mirror_last_name),
              mirror_locale=COALESCE(user_profiles.mirror_locale,excluded.mirror_locale)
            """, connection);
        command.Parameters.AddWithValue("user", userId);
        AddNullable(command, "first", Bounded(firstName, 80));
        AddNullable(command, "last", Bounded(lastName, 80));
        AddNullable(command, "locale", Bounded(locale, 35));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<OwnProfile> GetOwnAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using (var create = new NpgsqlCommand(
            "INSERT INTO user_profiles(user_id) VALUES (@user) ON CONFLICT DO NOTHING", connection))
        {
            create.Parameters.AddWithValue("user", userId);
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var command = new NpgsqlCommand("""
            SELECT mirror_first_name,mirror_last_name,mirror_locale,display_name,time_zone,
                   avatar_object_key IS NOT NULL FROM user_profiles WHERE user_id=@user AND deleted_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Profile unavailable.");
        return new OwnProfile(userId, Read(reader, 0), Read(reader, 1), Read(reader, 2),
            Read(reader, 3), reader.GetString(4), reader.GetBoolean(5));
    }

    public async Task<OwnProfile> UpdateOwnAsync(Guid userId, UpdateOwnProfile input,
        CancellationToken ct = default)
    {
        if (!ProfileValidation.ValidOwn(input)) throw new ArgumentException("Invalid profile.");
        await GetOwnAsync(userId, ct);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.actor_sub", userId, ct);
        await using (var update = new NpgsqlCommand("""
            UPDATE user_profiles SET display_name=COALESCE(@display,display_name),
              time_zone=COALESCE(@zone,time_zone),
              mirror_first_name=COALESCE(@first,mirror_first_name),
              mirror_last_name=COALESCE(@last,mirror_last_name),
              mirror_locale=COALESCE(@locale,mirror_locale),updated_at=now()
            WHERE user_id=@user AND deleted_at IS NULL
            """, connection, tx))
        {
            update.Parameters.AddWithValue("user", userId);
            AddNullable(update, "display", input.DisplayName);
            AddNullable(update, "zone", input.TimeZone);
            AddNullable(update, "first", input.FirstName);
            AddNullable(update, "last", input.LastName);
            AddNullable(update, "locale", input.Locale);
            await update.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(connection, tx, null, userId, userId, "profile.account.updated", null, ct);
        var changed = new List<string>();
        if (input.DisplayName is not null) changed.Add("displayName");
        if (input.TimeZone is not null) changed.Add("timeZone");
        if (changed.Count > 0)
            await WriteUpdatedEventAsync(connection, tx, userId, changed.ToArray(), ct);
        await tx.CommitAsync(ct);
        return await GetOwnAsync(userId, ct);
    }

    public async Task<Dictionary<string, JsonElement>> GetPreferencesAsync(Guid userId,
        CancellationToken ct = default)
    {
        await GetOwnAsync(userId, ct);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT key,value FROM user_preferences WHERE user_id=@user", connection);
        command.Parameters.AddWithValue("user", userId);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0), JsonDocument.Parse(reader.GetString(1)).RootElement.Clone());
        return result;
    }

    public async Task<Dictionary<string, JsonElement>> UpdatePreferencesAsync(Guid userId,
        UpdatePreferences input, CancellationToken ct = default)
    {
        if (!ProfileValidation.ValidPreferences(input)) throw new ArgumentException("Invalid preferences.");
        await GetOwnAsync(userId, ct);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.actor_sub", userId, ct);
        var values = new Dictionary<string, object?> {
            ["language"] = input.Language, ["timeZone"] = input.TimeZone,
            ["dateFormat"] = input.DateFormat, ["notifications"] = input.Notifications,
            ["theme"] = input.Theme
        };
        foreach (var (key, value) in values)
        {
            if (value is null) continue;
            await using var command = new NpgsqlCommand("""
                INSERT INTO user_preferences(user_id,key,value) VALUES (@user,@key,CAST(@value AS jsonb))
                ON CONFLICT(user_id,key) DO UPDATE SET value=excluded.value
                """, connection, tx);
            command.Parameters.AddWithValue("user", userId);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("value", JsonSerializer.Serialize(value));
            await command.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(connection, tx, null, userId, userId, "profile.preferences.updated", null, ct);
        await WriteUpdatedEventAsync(connection, tx, userId, ["preferences"], ct);
        await tx.CommitAsync(ct);
        return await GetPreferencesAsync(userId, ct);
    }

    public async Task<MemberProfile?> GetMemberAsync(Guid tenantId, Guid userId, bool own,
        CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.tenant_id", tenantId, ct);
        if (own)
        {
            await using var create = new NpgsqlCommand("""
                INSERT INTO tenant_member_profiles(tenant_id,user_id) VALUES (@tenant,@user)
                ON CONFLICT DO NOTHING
                """, connection, tx);
            create.Parameters.AddWithValue("tenant", tenantId);
            create.Parameters.AddWithValue("user", userId);
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var command = new NpgsqlCommand("""
            SELECT job_title,department,work_phone,directory_visibility
            FROM tenant_member_profiles WHERE tenant_id=@tenant AND user_id=@user
              AND status='active'
            """, connection, tx);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        MemberProfile? result = await reader.ReadAsync(ct)
            ? new MemberProfile(userId, Read(reader, 0), Read(reader, 1), Read(reader, 2), reader.GetString(3))
            : null;
        await reader.DisposeAsync();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<MemberProfile>> ListMembersAsync(Guid tenantId, Guid? after,
        int limit, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.tenant_id", tenantId, ct);
        await using var command = new NpgsqlCommand("""
            SELECT user_id,job_title,department,work_phone,directory_visibility
            FROM tenant_member_profiles
            WHERE tenant_id=@tenant AND status='active' AND directory_visibility='visible'
              AND (@after::uuid IS NULL OR user_id > @after)
            ORDER BY user_id LIMIT @limit
            """, connection, tx);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("after", after.HasValue ? after.Value : DBNull.Value);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        var result = new List<MemberProfile>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new MemberProfile(reader.GetGuid(0), Read(reader, 1), Read(reader, 2),
                Read(reader, 3), reader.GetString(4)));
        await reader.DisposeAsync();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<MemberProfile?> UpdateMemberAsync(Guid tenantId, Guid targetId, Guid actorId,
        UpdateMemberProfile input, string? reason, CancellationToken ct = default)
    {
        if (!ProfileValidation.ValidMember(input) || (actorId != targetId && string.IsNullOrWhiteSpace(reason)))
            throw new ArgumentException("Invalid member edit.");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.tenant_id", tenantId, ct);
        await SetLocalAsync(connection, "app.actor_sub", actorId, ct);
        // Only the gated own route can lazily create a member row.
        if (targetId == actorId)
        {
            await using var create = new NpgsqlCommand("""
                INSERT INTO tenant_member_profiles(tenant_id,user_id) VALUES (@tenant,@user)
                ON CONFLICT DO NOTHING
                """, connection, tx);
            create.Parameters.AddWithValue("tenant", tenantId);
            create.Parameters.AddWithValue("user", targetId);
            await create.ExecuteNonQueryAsync(ct);
        }
        await using var command = new NpgsqlCommand("""
            UPDATE tenant_member_profiles SET job_title=@job,department=@department,
              work_phone=@phone,directory_visibility=@visibility,updated_at=now()
            WHERE tenant_id=@tenant AND user_id=@user AND status='active'
            RETURNING job_title,department,work_phone,directory_visibility
            """, connection, tx);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", targetId);
        AddNullable(command, "job", input.JobTitle);
        AddNullable(command, "department", input.Department);
        AddNullable(command, "phone", input.WorkPhone);
        command.Parameters.AddWithValue("visibility", input.DirectoryVisibility);
        await using var reader = await command.ExecuteReaderAsync(ct);
        MemberProfile? result = await reader.ReadAsync(ct)
            ? new MemberProfile(targetId, Read(reader, 0), Read(reader, 1), Read(reader, 2), reader.GetString(3))
            : null;
        await reader.DisposeAsync();
        if (result is not null)
            await AuditAsync(connection, tx, tenantId, actorId, targetId,
                actorId == targetId ? "profile.member.self-updated" : "profile.member.admin-updated",
                reason, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<Guid> RequestExportAsync(Guid userId, CancellationToken ct = default)
    {
        await GetOwnAsync(userId, ct);
        var id = Guid.NewGuid();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.actor_sub", userId, ct);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO data_export_jobs(id,user_id,status) VALUES (@id,@user,'pending')
            """, connection, tx))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("user", userId);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO outbox(id,type,payload)
            VALUES (@id,'profile.export.requested.v1',jsonb_build_object(
              'specversion','1.0','id',@id,'type','profile.export.requested.v1',
              'source','excellore/profile','time',now(),'correlationid',@id,
              'actor',jsonb_build_object('sub',@user,'type','human'),
              'data',jsonb_build_object('userId',@user,'exportId',@id)))
            """, connection, tx))
        {
            outbox.Parameters.AddWithValue("id", id);
            outbox.Parameters.AddWithValue("user", userId);
            await outbox.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(connection, tx, null, userId, userId, "profile.export.requested", null, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<Guid> CreateAvatarUploadAsync(Guid userId, CreateAvatarUpload input,
        CancellationToken ct = default)
    {
        if (input.ContentType is not ("image/png" or "image/jpeg" or "image/webp") ||
            input.SizeBytes is < 16 or > AvatarProcessor.MaxInputBytes)
            throw new ArgumentException("Invalid avatar upload.");
        await GetOwnAsync(userId, ct);
        var id = Guid.NewGuid();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO avatar_uploads(id,user_id,object_key,status,content_type,size_bytes)
            VALUES (@id,@user,@key,'pending',@type,@size)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("key", $"avatars/{id:N}.webp");
        command.Parameters.AddWithValue("type", input.ContentType);
        command.Parameters.AddWithValue("size", (long)input.SizeBytes);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<bool> CompleteAvatarUploadAsync(Guid userId, Guid uploadId, byte[] input,
        IAvatarObjectStore objects, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.actor_sub", userId, ct);
        string key;
        string declaredType;
        long declaredSize;
        await using (var command = new NpgsqlCommand("""
            SELECT object_key,content_type,size_bytes FROM avatar_uploads
            WHERE id=@id AND user_id=@user AND status='pending' FOR UPDATE
            """, connection, tx))
        {
            command.Parameters.AddWithValue("id", uploadId);
            command.Parameters.AddWithValue("user", userId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            key = reader.GetString(0);
            declaredType = reader.GetString(1);
            declaredSize = reader.GetInt64(2);
        }
        if (declaredSize != input.Length) return false;
        var processed = AvatarProcessor.Reencode(input, declaredType);
        if (processed is null) return false;
        string? previousKey;
        await using (var command = new NpgsqlCommand("""
            SELECT avatar_object_key FROM user_profiles
            WHERE user_id=@user AND deleted_at IS NULL FOR UPDATE
            """, connection, tx))
        {
            command.Parameters.AddWithValue("user", userId);
            previousKey = await command.ExecuteScalarAsync(ct) is string value ? value : null;
        }
        await objects.PutAsync(key, processed, ct);
        try
        {
            await using (var command = new NpgsqlCommand("""
                UPDATE avatar_uploads SET status='processed'
                WHERE id=@id AND user_id=@user AND status='pending'
                """, connection, tx))
            {
                command.Parameters.AddWithValue("id", uploadId);
                command.Parameters.AddWithValue("user", userId);
                if (await command.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Upload state changed.");
            }
            await using (var command = new NpgsqlCommand("""
                UPDATE user_profiles SET avatar_object_key=@key,updated_at=now()
                WHERE user_id=@user AND deleted_at IS NULL
                """, connection, tx))
            {
                command.Parameters.AddWithValue("key", key);
                command.Parameters.AddWithValue("user", userId);
                await command.ExecuteNonQueryAsync(ct);
            }
            await AuditAsync(connection, tx, null, userId, userId, "profile.avatar.updated", null, ct);
            await WriteUpdatedEventAsync(connection, tx, userId, ["avatar"], ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await objects.DeleteAsync(key, ct);
            throw;
        }
        if (previousKey is not null && previousKey != key)
            await objects.DeleteAsync(previousKey, ct);
        return true;
    }

    public async Task<byte[]?> GetAvatarAsync(Guid userId, IAvatarObjectStore objects,
        CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT avatar_object_key FROM user_profiles WHERE user_id=@user AND deleted_at IS NULL
            """, connection);
        command.Parameters.AddWithValue("user", userId);
        var key = await command.ExecuteScalarAsync(ct) is string value ? value : null;
        return key is null ? null : await objects.GetAsync(key, ct);
    }

    public async Task RemoveAvatarAsync(Guid userId, IAvatarObjectStore objects,
        CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.actor_sub", userId, ct);
        string? key;
        await using (var command = new NpgsqlCommand("""
            SELECT avatar_object_key FROM user_profiles WHERE user_id=@user FOR UPDATE
            """, connection, tx))
        {
            command.Parameters.AddWithValue("user", userId);
            key = await command.ExecuteScalarAsync(ct) is string value ? value : null;
        }
        if (key is not null) await objects.DeleteAsync(key, ct);
        await using (var command = new NpgsqlCommand("""
            UPDATE user_profiles SET avatar_object_key=NULL,updated_at=now() WHERE user_id=@user
            """, connection, tx))
        {
            command.Parameters.AddWithValue("user", userId);
            await command.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(connection, tx, null, userId, userId, "profile.avatar.removed", null, ct);
        await WriteUpdatedEventAsync(connection, tx, userId, ["avatar"], ct);
        await tx.CommitAsync(ct);
    }

    public async Task<string?> GetExportStatusAsync(Guid userId, Guid jobId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT status FROM data_export_jobs WHERE id=@id AND user_id=@user", connection);
        command.Parameters.AddWithValue("id", jobId);
        command.Parameters.AddWithValue("user", userId);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    public async Task<byte[]?> DownloadExportAsync(Guid userId, Guid jobId,
        Excellore.Platform.IPrivateExportStore objects, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM data_export_jobs
            WHERE id=@id AND user_id=@user AND status='ready' AND expires_at > now()
            """, connection);
        command.Parameters.AddWithValue("id", jobId);
        command.Parameters.AddWithValue("user", userId);
        return await command.ExecuteScalarAsync(ct) is null ? null :
            await objects.GetAsync(jobId, "complete", ct);
    }

    public async Task<Guid> RequestDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetLocalAsync(connection, "app.actor_sub", userId, ct);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO outbox(id,type,payload)
            VALUES (@id,'profile.deletion.requested.v1',
              jsonb_build_object('specversion','1.0','id',@id,
                'type','profile.deletion.requested.v1','source','excellore/profile',
                'time',now(),'correlationid',@id,'actor',
                jsonb_build_object('sub',@user,'type','human'),
                'data',jsonb_build_object('userId',@user)))
            """, connection, tx))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("user", userId);
            await command.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(connection, tx, null, userId, userId, "profile.deletion.requested", null, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<bool> RecordInboxAsync(Guid eventId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO inbox(event_id) VALUES (@id) ON CONFLICT DO NOTHING", connection);
        command.Parameters.AddWithValue("id", eventId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task SetLocalAsync(NpgsqlConnection connection, string key, Guid value,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT set_config(@key,@value,true)", connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", value.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        Guid? tenantId, Guid actorId, Guid targetId, string action, string? reason, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO profile_audit_events(id,tenant_id,actor_sub,target_sub,action,reason)
            VALUES (@id,@tenant,@actor,@target,@action,@reason)
            """, connection, tx);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", tenantId.HasValue ? tenantId.Value : DBNull.Value);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("target", targetId);
        command.Parameters.AddWithValue("action", action);
        AddNullable(command, "reason", reason ?? "self-service");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteUpdatedEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction tx, Guid userId, string[] changed, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO outbox(id,type,payload)
            VALUES (@id,'profile.updated.v1',
              jsonb_build_object('specversion','1.0','id',@id,
                'type','profile.updated.v1','source','excellore/profile',
                'time',now(),'correlationid',@id,'actor',
                jsonb_build_object('sub',@user,'type','human'),
                'data',jsonb_build_object('userId',@user,'changed',@changed)))
            """, connection, tx);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("changed", changed);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddNullable(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    private static string? Read(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
