using System.Security.Cryptography;
using Excellore.Auth;
using Excellore.Contracts;
using Excellore.Profile.Application;
using Excellore.Profile.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;
using SkiaSharp;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;

namespace Excellore.Profile.Tests;

public sealed class ProfileIsolationTests
{
    [Fact]
    public async Task TenantRowsAreIsolatedAndDirectoryHidesSuspendedAndHiddenMembers()
    {
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var postgres = new PostgreSqlBuilder("postgres:16.12")
            .WithPassword(adminPassword).Build();
        await postgres.StartAsync();
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        await Execute(admin, $"CREATE ROLE profile_migrator LOGIN PASSWORD '{migratorPassword}' NOSUPERUSER NOBYPASSRLS");
        await Execute(admin, $"CREATE ROLE profile_app LOGIN PASSWORD '{appPassword}' NOSUPERUSER NOBYPASSRLS");
        await Execute(admin, "CREATE ROLE profile_publisher LOGIN NOSUPERUSER NOBYPASSRLS");
        await Execute(admin, "CREATE ROLE profile_worker LOGIN NOSUPERUSER NOBYPASSRLS");
        await Execute(admin, "GRANT USAGE,CREATE ON SCHEMA public TO profile_migrator");
        await Execute(admin, "GRANT USAGE ON SCHEMA public TO profile_app,profile_publisher,profile_worker");
        await using var migrator = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = "profile_migrator", Password = migratorPassword }.ConnectionString);
        await migrator.OpenAsync();
        await Execute(migrator, await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "001_initial.sql")));
        await Execute(migrator, await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "007_user_erasure.sql")));
        var appString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = "profile_app", Password = appPassword }.ConnectionString;
        await using var db = NpgsqlDataSource.Create(appString);
        var repository = new ProfileRepository(db);
        var firstTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var suspended = Guid.NewGuid();
        await SeedMember(db, firstTenant, visible, "visible", "active");
        await SeedMember(db, firstTenant, hidden, "hidden", "active");
        await SeedMember(db, firstTenant, suspended, "visible", "suspended");
        await SeedMember(db, otherTenant, visible, "visible", "active");

        Assert.Single(await repository.ListMembersAsync(firstTenant, null, 50));
        Assert.Null(await repository.GetMemberAsync(firstTenant, suspended, false));
        Assert.Null(await repository.GetMemberAsync(otherTenant, hidden, false));
        Assert.Single(await repository.ListMembersAsync(otherTenant, null, 50));
        var adminEdit = await repository.UpdateMemberAsync(firstTenant, visible, hidden,
            new UpdateMemberProfile("Engineer", null, null, "visible"), "Directory correction");
        Assert.Equal("Engineer", adminEdit?.JobTitle);
        Assert.Null((await repository.GetMemberAsync(otherTenant, visible, false))?.JobTitle);
        Assert.Equal(1L, await ScalarLong(admin,
            "SELECT count(*) FROM profile_audit_events WHERE tenant_id=@tenant AND action='profile.member.admin-updated'",
            firstTenant));
        await using var raw = await db.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM tenant_member_profiles", raw);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        var eventId = Guid.NewGuid();
        Assert.True(await repository.RecordInboxAsync(eventId));
        Assert.False(await repository.RecordInboxAsync(eventId));
        var deletionId = await repository.RequestDeletionAsync(visible);
        await using var eventCommand = new NpgsqlCommand(
            "SELECT payload::text FROM outbox WHERE id=@id", admin);
        eventCommand.Parameters.AddWithValue("id", deletionId);
        var envelope = ProfileDeletionRequestedV1.Parse(
            System.Text.Encoding.UTF8.GetBytes((string)(await eventCommand.ExecuteScalarAsync())!));
        Assert.Equal(visible, envelope.Data!.UserId);
        await repository.UpdatePreferencesAsync(visible,
            new UpdatePreferences("en", null, "iso", true, "system"));
        await using var updateEvent = new NpgsqlCommand(
            "SELECT payload::text FROM outbox WHERE type='profile.updated.v1' LIMIT 1", admin);
        var updated = ProfileUpdatedV1.Parse(System.Text.Encoding.UTF8.GetBytes(
            (string)(await updateEvent.ExecuteScalarAsync())!));
        Assert.Equal(visible, updated.Data!.UserId);
        Assert.Equal(["preferences"], updated.Data.Changed!);
        using var avatarBitmap = new SKBitmap(16, 16);
        avatarBitmap.Erase(SKColors.SkyBlue);
        using var avatarImage = SKImage.FromBitmap(avatarBitmap);
        using var avatarPng = avatarImage.Encode(SKEncodedImageFormat.Png, 100);
        var avatarBytes = avatarPng.ToArray();
        var uploadId = await repository.CreateAvatarUploadAsync(visible,
            new CreateAvatarUpload("image/png", avatarBytes.Length));
        var store = new MemoryAvatarStore();
        var attempts = await Task.WhenAll(
            repository.CompleteAvatarUploadAsync(visible, uploadId, avatarBytes, store),
            repository.CompleteAvatarUploadAsync(visible, uploadId, avatarBytes, store));
        Assert.Single(attempts, value => value);
        Assert.NotNull(await repository.GetAvatarAsync(visible, store));
    }

    [Fact]
    public async Task OwnIdentityServiceAlwaysUsesAuthenticatedSubject()
    {
        var subject = Guid.NewGuid();
        var target = new CaptureIdentityClient();
        var service = new OwnIdentityService(new FakeCurrentUser(subject), target);
        Assert.True(await service.UpdateAsync(new UpdateOwnProfile("A", "B", "en", null, null)));
        Assert.Equal(subject, target.UserId);
    }

    [Fact]
    public void AvatarRejectsMismatchedTypeAndReencodesWithoutSourceBytes()
    {
        using var bitmap = new SKBitmap(24, 24);
        bitmap.Erase(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap);
        using var source = image.Encode(SKEncodedImageFormat.Png, 100);
        var input = source.ToArray();
        Assert.Null(AvatarProcessor.Reencode(input, "image/jpeg"));
        Assert.Null(AvatarProcessor.Reencode([1, 2, 3, 4], "image/png"));
        var output = AvatarProcessor.Reencode(input, "image/png");
        Assert.NotNull(output);
        Assert.NotEqual(input, output);
        using var decoded = SKCodec.Create(SKData.CreateCopy(output));
        Assert.NotNull(decoded);
        Assert.Equal(SKEncodedImageFormat.Webp, decoded.EncodedFormat);
        Assert.Equal(24, decoded.Info.Width);
        using var sourceWebp = image.Encode(SKEncodedImageFormat.Webp, 85);
        Assert.NotNull(AvatarProcessor.Reencode(sourceWebp.ToArray(), "image/webp"));
        var sample = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAABgAAAAYCAYAAADgdz34AAAABHNCSVQICAgIfAhkiAAAAChJREFUSIlj/F8f8J+BhoCJloaPWjBqwagFoxaMWjBqwagFoxZQDwAAVi0C/U9K/LQAAAAASUVORK5CYII=");
        Assert.NotNull(AvatarProcessor.Reencode(sample, "image/png"));
    }

    [Fact]
    public async Task OwnIdentityUpdatePreservesOtherKeycloakAttributes()
    {
        var userId = Guid.NewGuid();
        var handler = new IdentityHttpHandler(userId);
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auth:Authority"] = "https://id.example.invalid/realms/excellore",
                ["Identity:Authority"] = "http://keycloak.private.svc.cluster.local/realms/excellore",
                ["Identity:PrivateHttpHost"] = "keycloak.private.svc.cluster.local",
                ["Identity:ClientSecret"] = "test-only"
            }).Build();
        var client = new KeycloakOwnIdentityClient(http, config);
        Assert.True(await client.UpdateAsync(userId, "New", null, "en", default));
        Assert.All(handler.UserRequests, path =>
            Assert.Contains(userId.ToString("D"), path, StringComparison.Ordinal));
        Assert.All(handler.Requests, uri =>
            Assert.Equal("keycloak.private.svc.cluster.local", uri.Host));
        using var changed = JsonDocument.Parse(handler.UpdatedBody!);
        Assert.Equal("New", changed.RootElement.GetProperty("firstName").GetString());
        Assert.Equal("person@example.invalid", changed.RootElement.GetProperty("email").GetString());
        Assert.Equal("on", changed.RootElement.GetProperty("attributes")
            .GetProperty("unrelated")[0].GetString());
        Assert.Equal("en", changed.RootElement.GetProperty("attributes")
            .GetProperty("locale")[0].GetString());
    }

    private static async Task SeedMember(NpgsqlDataSource db, Guid tenant, Guid user,
        string visibility, string status)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id',@tenant,true)", connection, tx))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString("D"));
            await set.ExecuteNonQueryAsync();
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tenant_member_profiles(tenant_id,user_id,directory_visibility,status)
            VALUES (@tenant,@user,@visibility,@status)
            """, connection, tx);
        insert.Parameters.AddWithValue("tenant", tenant);
        insert.Parameters.AddWithValue("user", user);
        insert.Parameters.AddWithValue("visibility", visibility);
        insert.Parameters.AddWithValue("status", status);
        await insert.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> ScalarLong(NpgsqlConnection connection, string sql, Guid tenant)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant", tenant);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FakeCurrentUser(Guid id) : ICurrentUser
    {
        public Guid UserId => id;
        public string? Email => null;
        public string? Acr => "2";
        public bool HasRecentMfa => true;
    }
    private sealed class CaptureIdentityClient : IOwnIdentityClient
    {
        public Guid UserId { get; private set; }
        public Task<bool> UpdateAsync(Guid userId, string? firstName, string? lastName,
            string? locale, CancellationToken cancellationToken)
        {
            UserId = userId;
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<OwnSession>?> GetSessionsAsync(Guid userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OwnSession>?>([]);
        public Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class MemoryAvatarStore : IAvatarObjectStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> objects = new();
        public Task PutAsync(string key, byte[] image, CancellationToken cancellationToken)
        {
            objects[key] = image;
            return Task.CompletedTask;
        }
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(objects.TryGetValue(key, out var image) ? image : null);
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
    private sealed class IdentityHttpHandler(Guid userId) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string> UserRequests { get; } = [];
        public string? UpdatedBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("{\"access_token\":\"test-token\"}") };
            UserRequests.Add(request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(JsonSerializer.Serialize(new {
                        id = userId.ToString("D"), email = "person@example.invalid",
                        firstName = "Old", attributes = new { unrelated = new[] { "on" } }
                    })) };
            UpdatedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
