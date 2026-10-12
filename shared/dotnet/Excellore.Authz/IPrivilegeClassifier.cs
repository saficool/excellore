namespace Excellore.Authz;

// Apps replace this with their code-first permission catalog. Unknown permissions require step-up.
public interface IPrivilegeClassifier
{
    bool IsPrivileged(string permission);
}

public sealed class ConservativePrivilegeClassifier : IPrivilegeClassifier
{
    public bool IsPrivileged(string permission) => true;
}
