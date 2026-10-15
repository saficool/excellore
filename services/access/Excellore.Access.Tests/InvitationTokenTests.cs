using Excellore.Access.Infrastructure;

namespace Excellore.Access.Tests;

public sealed class InvitationTokenTests
{
    [Fact]
    public void Issued_token_is_random_and_only_its_secret_hash_is_stored()
    {
        var tenantId = Guid.NewGuid();
        var first = InvitationToken.Issue(tenantId);
        var second = InvitationToken.Issue(tenantId);
        Assert.NotEqual(first.Value, second.Value);
        Assert.NotEqual(first.SecretHash, second.SecretHash);
        Assert.True(InvitationToken.TryRead(first.Value, out var decodedTenant, out var hash));
        Assert.Equal(tenantId, decodedTenant);
        Assert.True(InvitationToken.Matches(first.SecretHash, hash));
        Assert.False(InvitationToken.Matches(second.SecretHash, hash));
    }

    [Fact]
    public void Malformed_or_changed_tokens_are_rejected()
    {
        var token = InvitationToken.Issue(Guid.NewGuid());
        Assert.False(InvitationToken.TryRead(null, out _, out _));
        Assert.False(InvitationToken.TryRead(token.Value[..^1], out _, out _));
        Assert.False(InvitationToken.TryRead(token.Value.Replace('.', '/'), out _, out _));
        var changed = token.Value[..37] + (token.Value[37] == 'A' ? "B" : "A") + token.Value[38..];
        Assert.True(InvitationToken.TryRead(changed, out _, out var changedHash));
        Assert.False(InvitationToken.Matches(token.SecretHash, changedHash));
    }
}
