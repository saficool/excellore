namespace Excellore.Authz;

// Apps replace this with their code-first permission catalog. Unknown permissions require step-up.
public interface IPrivilegeClassifier
{
    bool IsPrivileged(string permission);

    ValueTask<bool> IsPrivilegedAsync(string permission, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(IsPrivileged(permission));
}

public sealed class ConservativePrivilegeClassifier : IPrivilegeClassifier
{
    public bool IsPrivileged(string permission) => true;
}
