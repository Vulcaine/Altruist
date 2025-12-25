namespace Altruist.Persistence;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PrefabAttribute : Attribute
{
    public string Id { get; }
    public PrefabAttribute(string id) => Id = id ?? throw new ArgumentNullException(nameof(id));
}

/// <summary>
/// Marks the root component (the FROM anchor).
/// Property type must be an IVaultModel (single).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PrefabComponentRootAttribute : Attribute
{
}

/// <summary>
/// Marks a component reference that can be queried and included.
/// No nesting: Principal must be the root property name.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PrefabComponentRefAttribute : Attribute
{
    /// <summary>
    /// Name of the principal prefab component property (must be root due to "no nesting").
    /// Example: nameof(Character)
    /// </summary>
    public string Principal { get; }

    /// <summary>
    /// For COLLECTION refs: FK property name on dependent model, e.g. nameof(EquipmentItemVault.CharacterId).
    ///
    /// For SINGLE refs: FK property name on the principal model pointing to dependent PK, e.g. nameof(CharacterVault.GuildId).
    /// </summary>
    public string ForeignKey { get; }

    /// <summary>
    /// For SINGLE refs: principal key property name on the dependent model (defaults to StorageId).
    /// </summary>
    public string PrincipalKey { get; set; } = nameof(IVaultModel.StorageId);

    public PrefabComponentRefAttribute(string principal, string foreignKey)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        ForeignKey = foreignKey ?? throw new ArgumentNullException(nameof(foreignKey));
    }
}
