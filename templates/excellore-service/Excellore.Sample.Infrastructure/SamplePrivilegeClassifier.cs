using Excellore.Authz;

namespace Excellore.Sample.Infrastructure;

public sealed class SamplePrivilegeClassifier : IPrivilegeClassifier
{
    // Unknown permissions still require step-up. Update this from permissions.yaml.
    public bool IsPrivileged(string permission) =>
        !string.Equals(permission, "__APP_SLUG__.item.read", StringComparison.Ordinal);
}
