namespace Altruist.Persistence;

/// <summary>
/// Tags a <see cref="PrefabModel"/> class with a stable identifier. A prefab is an in-memory aggregate of
/// several related vault models (one root plus referenced components) that is loaded and saved together
/// through <see cref="IPrefabs"/>; it has no table of its own.
/// </summary>
/// <remarks>
/// The prefab's structure is defined by the property attributes, not by this attribute:
/// exactly one <see cref="PrefabComponentRootAttribute"/> property and any number of
/// <see cref="PrefabComponentRefAttribute"/> and <see cref="PrefabComponentOwnedAttribute"/> properties (validated by
/// <see cref="PrefabDocument"/> on first use).
/// The framework does not currently read <see cref="Id"/> (prefab discovery is by deriving from
/// <see cref="PrefabModel"/>), so the attribute is descriptive. Use a plain vault model
/// (<see cref="Altruist.UORM.VaultAttribute"/>) when you only need one table; use a prefab when several tables
/// are always loaded/saved as one unit.
/// </remarks>
/// <example>
/// <code>
/// [Prefab("account-profile")]
/// public sealed class AccountPrefab : PrefabModel
/// {
///     [PrefabComponentRoot]
///     public AccountVault Account { get; set; } = default!;
///
///     // Collection ref: FK lives on the dependent rows and points at the root's StorageId.
///     [PrefabComponentRef(nameof(Account), nameof(SessionVault.AccountId))]
///     public List&lt;SessionVault&gt; Sessions { get; set; } = new();
///
///     // Single ref: FK lives on the root and points at the dependent's key (StorageId by default).
///     [PrefabComponentRef(nameof(Account), nameof(AccountVault.SettingsId))]
///     public SettingsVault? Settings { get; set; }
///
///     // Owned one-to-one: FK lives on the dependent row and points at the root's StorageId.
///     [PrefabComponentOwned(nameof(Account), nameof(ProfileVault.AccountId))]
///     public ProfileVault? Profile { get; set; }
/// }
///
/// var prefab = await prefabs.Query&lt;AccountPrefab&gt;()
///     .Where(p =&gt; p.Account.StorageId == id)
///     .IncludeAll()
///     .FirstOrDefaultAsync();
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PrefabAttribute : Attribute
{
    /// <summary>Stable prefab identifier given to the constructor (never null).</summary>
    public string Id { get; }
    /// <summary>Tags the class with a prefab identifier.</summary>
    /// <param name="id">Stable, unique prefab id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    public PrefabAttribute(string id) => Id = id ?? throw new ArgumentNullException(nameof(id));
}

/// <summary>
/// Marks the root component (the FROM anchor) of a <see cref="PrefabModel"/>: the vault model every other
/// component hangs off. Exactly one property per prefab must carry it.
/// </summary>
/// <remarks>
/// The property type must be a single <see cref="IVaultModel"/> (not a collection) and the property must be
/// settable; otherwise <see cref="PrefabDocument"/> throws <see cref="InvalidOperationException"/> when the prefab
/// metadata is first built. Queries on the prefab filter on the root table. See <see cref="PrefabAttribute"/> for an example.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PrefabComponentRootAttribute : Attribute
{
}

/// <summary>
/// Marks a component reference that can be queried and included.
/// No nesting: Principal must be the root property name.
/// </summary>
/// <remarks>
/// The property type decides the relation kind: a single <see cref="IVaultModel"/> is a SINGLE ref (FK on the root
/// pointing at the dependent's <see cref="PrincipalKey"/>); <c>List&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c> or
/// <c>IEnumerable&lt;T&gt;</c> of <see cref="IVaultModel"/> is a COLLECTION ref (FK on each dependent row pointing at
/// the root's <c>StorageId</c>). The property must be settable. Refs are loaded only when included
/// (<c>IPrefabQuery.Include</c> / <c>IncludeAll</c>). Invalid declarations throw
/// <see cref="InvalidOperationException"/> when <see cref="PrefabDocument"/> first builds the metadata.
/// See <see cref="PrefabAttribute"/> for an example.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PrefabComponentRefAttribute : Attribute
{
    /// <summary>
    /// Name of the principal prefab component property (must be root due to "no nesting").
    /// Example: <c>nameof(Account)</c>.
    /// </summary>
    public string Principal { get; }

    /// <summary>
    /// For COLLECTION refs: FK property name on the dependent model pointing at the root's <c>StorageId</c>,
    /// e.g. <c>nameof(SessionVault.AccountId)</c>.
    /// For SINGLE refs: FK property name on the root (principal) model pointing at the dependent's
    /// <see cref="PrincipalKey"/>, e.g. <c>nameof(AccountVault.SettingsId)</c>. Required (blank fails validation).
    /// </summary>
    public string ForeignKey { get; }

    /// <summary>
    /// For SINGLE refs: principal key property name on the dependent model (defaults to StorageId).
    /// Ignored for COLLECTION refs, which always join on the root's <c>StorageId</c>.
    /// </summary>
    public string PrincipalKey { get; set; } = nameof(IVaultModel.StorageId);

    /// <summary>Declares a reference from the root component to this property's component(s).</summary>
    /// <param name="principal">Name of the root property (use <c>nameof</c>); any other value fails validation.</param>
    /// <param name="foreignKey">FK property name; see <see cref="ForeignKey"/> for which side it lives on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> or <paramref name="foreignKey"/> is null.</exception>
    public PrefabComponentRefAttribute(string principal, string foreignKey)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        ForeignKey = foreignKey ?? throw new ArgumentNullException(nameof(foreignKey));
    }
}

/// <summary>
/// Marks a one-to-one component owned by the root: a single <see cref="IVaultModel"/> whose foreign key points at
/// the root's <c>StorageId</c>, so the key lives on the dependent row (e.g. a profile keyed by its account id).
/// No nesting: Principal must be the root property name.
/// </summary>
/// <remarks>
/// The counterpart of a single <see cref="PrefabComponentRefAttribute"/>, whose key lives on the root. Loaded only
/// when included, like the other refs; stays null when the root has no row. A root may own at most one row: loading
/// a second one throws <see cref="InvalidOperationException"/>, so give the foreign key a unique key
/// (<see cref="Altruist.UORM.VaultUniqueKeyAttribute"/>). Invalid declarations throw
/// <see cref="InvalidOperationException"/> when <see cref="PrefabDocument"/> first builds the metadata.
/// </remarks>
/// <example>
/// <code>
/// [PrefabComponentOwned(nameof(Account), nameof(ProfileVault.AccountId))]
/// public ProfileVault? Profile { get; set; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class PrefabComponentOwnedAttribute : Attribute
{
    /// <summary>Name of the root component property (use <c>nameof</c>); any other value fails validation.</summary>
    public string Principal { get; }

    /// <summary>FK property name on the owned model pointing at the root's <c>StorageId</c>, e.g. <c>nameof(ProfileVault.AccountId)</c>.</summary>
    public string ForeignKey { get; }

    /// <summary>Declares a one-to-one component owned by the root.</summary>
    /// <param name="principal">Name of the root property (use <c>nameof</c>).</param>
    /// <param name="foreignKey">FK property name on the owned model.</param>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> or <paramref name="foreignKey"/> is null.</exception>
    public PrefabComponentOwnedAttribute(string principal, string foreignKey)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        ForeignKey = foreignKey ?? throw new ArgumentNullException(nameof(foreignKey));
    }
}

/// <summary>
/// Reserved component metadata for prefab properties (auto-load ordering and relation key).
/// </summary>
/// <remarks>
/// Nothing in the framework reads this attribute today: prefab structure and loading are driven solely by
/// <see cref="PrefabComponentRootAttribute"/> and <see cref="PrefabComponentRefAttribute"/>. Prefer those; this
/// attribute has no runtime effect.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PrefabComponentAttribute : Attribute
{
    /// <summary>
    /// Name of the prefab component this component should auto-load after
    /// (e.g. <c>AutoLoadOn = nameof(Account)</c>). Reserved; currently not read.
    /// </summary>
    public string? AutoLoadOn { get; set; }

    /// <summary>
    /// Name of the relation key used to tie this component to the AutoLoadOn component.
    /// Reserved for richer relations; currently neither validated nor used.
    /// </summary>
    public string? RelationKey { get; set; }
}
