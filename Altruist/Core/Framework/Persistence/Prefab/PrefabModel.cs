// PrefabModel.cs

namespace Altruist.Persistence;

/// <summary>
/// In-memory aggregate of interconnected vault models.
/// No prefab table; persistence is delegated to IPrefabs.
/// </summary>
/// <remarks>
/// Derive from it, mark exactly one settable <see cref="IVaultModel"/> property with
/// <see cref="PrefabComponentRootAttribute"/>, and mark related components with
/// <see cref="PrefabComponentRefAttribute"/> (single <see cref="IVaultModel"/>, or <see cref="List{T}"/> /
/// <see cref="IReadOnlyList{T}"/> / <see cref="IEnumerable{T}"/> of one). No nesting: every ref's principal is the root.
/// A public parameterless constructor is required for querying. Load via <see cref="IPrefabs.Query{TPrefab}"/>.
/// Structure is validated lazily by <see cref="PrefabDocument.Get(Type)"/>.
/// </remarks>
/// <example>
/// <code>
/// public sealed class PlayerPrefab : PrefabModel
/// {
///     [PrefabComponentRoot] public AccountVault Account { get; set; } = default!;
///     // collection: FK on the dependent model pointing at the root's StorageId
///     [PrefabComponentRef(nameof(Account), nameof(ItemVault.AccountId))]
///     public List&lt;ItemVault&gt; Items { get; set; } = default!;
///     // single: FK on the root pointing at the dependent's StorageId
///     [PrefabComponentRef(nameof(Account), nameof(AccountVault.GuildId))]
///     public GuildVault? Guild { get; set; }
/// }
/// </code>
/// </example>
public abstract class PrefabModel : IPrefabModel
{
    /// <summary>
    /// Save all dirty components reachable from this prefab using the current database provider.
    /// Shortcut for <see cref="IPrefabs.SaveAsync"/> resolved from the global DI container.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task SaveAsync(CancellationToken ct = default)
        => Dependencies.Inject<IPrefabs>().SaveAsync(this, ct);

    /// <summary>
    /// Save a single component (by prefab component property name), e.g. nameof(CharacterPrefab.BagInstances).
    /// Should not fall back to loading/hydrating; only persists what is already on the prefab.
    /// Prefer this over <see cref="SaveAsync"/> when only one component changed.
    /// </summary>
    /// <param name="componentName">The component property name (use <c>nameof</c>); the root's name saves only the root.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SaveComponentAsync(string componentName, CancellationToken ct = default)
        => Dependencies.Inject<IPrefabs>().SaveComponentAsync(this, componentName, ct);
}

/// <summary>Marker interface for prefab aggregates; derive from <see cref="PrefabModel"/> rather than implementing it directly.</summary>
public interface IPrefabModel { }
