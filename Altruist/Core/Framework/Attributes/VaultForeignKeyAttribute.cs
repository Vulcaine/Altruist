namespace Altruist;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class VaultForeignKeyAttribute : Attribute
{
    public Type PrincipalType { get; }
    public string PrincipalPropertyName { get; }

    public VaultForeignKeyAttribute(Type principalType, string principalPropertyName)
    {
        PrincipalType = principalType ?? throw new ArgumentNullException(nameof(principalType));
        PrincipalPropertyName = principalPropertyName ?? throw new ArgumentNullException(nameof(principalPropertyName));
    }
}
