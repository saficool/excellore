using System.Security.Cryptography;

namespace Excellore.Access.Infrastructure;

public sealed record IssuedInvitationToken(string Value, byte[] SecretHash);

public static class InvitationToken
{
    public static IssuedInvitationToken Issue(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.", nameof(tenantId));
        var secret = RandomNumberGenerator.GetBytes(32);
        var encoded = Convert.ToBase64String(secret).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
        return new IssuedInvitationToken($"{tenantId:D}.{encoded}", SHA256.HashData(secret));
    }

    public static bool TryRead(string? value, out Guid tenantId, out byte[] secretHash)
    {
        tenantId = Guid.Empty;
        secretHash = [];
        if (value is not { Length: 80 } || value[36] != '.' ||
            !Guid.TryParseExact(value[..36], "D", out tenantId) || tenantId == Guid.Empty)
            return false;
        var encoded = value[37..];
        if (encoded.Length != 43 || encoded.Any(character =>
                character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '-' or '_')))
            return false;
        try
        {
            var secret = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "=");
            if (secret.Length != 32) return false;
            secretHash = SHA256.HashData(secret);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool Matches(byte[] expectedHash, byte[] candidateHash) =>
        expectedHash.Length == 32 && candidateHash.Length == 32 &&
        CryptographicOperations.FixedTimeEquals(expectedHash, candidateHash);
}
