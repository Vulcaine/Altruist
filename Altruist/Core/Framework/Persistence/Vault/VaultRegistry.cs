/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/
using System.Collections.Concurrent;

namespace Altruist
{
    /// <summary>Registry entry describing one vault model: its stable type key, CLR type and keyspace.</summary>
    public sealed class VaultMetadata
    {
        /// <summary>Stable key for the model type (its full CLR name); safe to persist or send to clients.</summary>
        public string TypeKey { get; }
        /// <summary>The vault model CLR type.</summary>
        public Type ClrType { get; }
        /// <summary>Schema / keyspace the table lives in (<c>altruist</c> when registered blank).</summary>
        public string Keyspace { get; }

        /// <summary>Creates the entry.</summary>
        /// <param name="typeKey">Stable type key.</param>
        /// <param name="clrType">Model type.</param>
        /// <param name="keyspace">Keyspace; blank becomes <c>altruist</c>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="typeKey"/> or <paramref name="clrType"/> is null.</exception>
        public VaultMetadata(string typeKey, Type clrType, string keyspace)
        {
            TypeKey = typeKey ?? throw new ArgumentNullException(nameof(typeKey));
            ClrType = clrType ?? throw new ArgumentNullException(nameof(clrType));
            Keyspace = string.IsNullOrWhiteSpace(keyspace) ? "altruist" : keyspace;
        }
    }

    /// <summary>
    /// Process-wide catalogue of vault model types, used to resolve a model from a string type key (e.g. in admin
    /// tooling) and to enumerate all persisted models.
    /// </summary>
    /// <remarks>
    /// The SQL provider configuration calls <see cref="Register(Type, string)"/> for every <see cref="Altruist.UORM.VaultAttribute"/>
    /// model at startup. To query or save data, inject <see cref="IVault{TVaultModel}"/> instead: the instance and
    /// finder lookups (<see cref="GetVault{TModel}"/>, <see cref="FindByStorageIdAsync"/>) only work for models whose
    /// provider called <see cref="RegisterVaultInstance"/> / <see cref="RegisterFindByIdDelegate"/>, which the Postgres
    /// provider currently does not. Thread-safe.
    /// </remarks>
    public static class VaultRegistry
    {
        private static readonly ConcurrentDictionary<string, VaultMetadata> _byTypeKey =
            new(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<Type, VaultMetadata> _byClr =
            new();

        private static readonly ConcurrentDictionary<string, VaultMetadata> _bySimpleName =
            new(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, bool> _simpleNameCollision =
            new(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<Type, object> _vaultByClr =
            new();

        private static readonly ConcurrentDictionary<Type, Func<string, Task<IVaultModel?>>> _findById =
            new();

        /// <summary>Registers <typeparamref name="TModel"/> under its full-name type key. Re-registering overwrites.</summary>
        /// <typeparam name="TModel">The vault model type.</typeparam>
        /// <param name="keyspace">Schema / keyspace of its table.</param>
        public static void Register<TModel>(string keyspace)
            where TModel : class, IVaultModel
            => Register(typeof(TModel), keyspace);

        /// <summary>Returns every registered model.</summary>
        /// <returns>A snapshot of the registered entries.</returns>
        public static IReadOnlyCollection<VaultMetadata> GetAll()
        {
            return _byClr.Values.Distinct().ToArray();
        }

        /// <summary>Registers a model type under its full-name type key (also indexed by simple name when unambiguous).</summary>
        /// <param name="clrType">The vault model type.</param>
        /// <param name="keyspace">Schema / keyspace of its table.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is null.</exception>
        public static void Register(Type clrType, string keyspace)
        {
            if (clrType is null)
                throw new ArgumentNullException(nameof(clrType));

            var typeKey = GetDefaultTypeKey(clrType);
            var md = new VaultMetadata(typeKey, clrType, keyspace);

            _byTypeKey[typeKey] = md;
            _byClr[clrType] = md;

            var simple = clrType.Name;
            if (!_bySimpleName.TryAdd(simple, md))
            {
                _simpleNameCollision[simple] = true;
            }
        }

        /// <summary>Resolves a model by full-name type key, falling back to the simple class name when it is unique.</summary>
        /// <param name="typeKey">Full CLR name (preferred) or simple class name.</param>
        /// <returns>The entry.</returns>
        /// <exception cref="ArgumentException"><paramref name="typeKey"/> is blank.</exception>
        /// <exception cref="InvalidOperationException">The key is unknown or the simple name is ambiguous.</exception>
        public static VaultMetadata GetByTypeKey(string typeKey)
        {
            if (string.IsNullOrWhiteSpace(typeKey))
                throw new ArgumentException("Type key is required.", nameof(typeKey));

            if (_byTypeKey.TryGetValue(typeKey, out var md))
                return md;

            if (_simpleNameCollision.ContainsKey(typeKey))
                throw new InvalidOperationException(
                    $"VaultRegistry: simple type name '{typeKey}' is ambiguous; store the full name instead.");

            if (_bySimpleName.TryGetValue(typeKey, out md))
                return md;

            throw new InvalidOperationException($"VaultRegistry: type key '{typeKey}' not registered.");
        }

        /// <summary>Resolves the entry for a registered model type.</summary>
        /// <param name="clrType">The model type.</param>
        /// <returns>The entry.</returns>
        /// <exception cref="InvalidOperationException">The type is not registered.</exception>
        public static VaultMetadata GetByClr(Type clrType)
        {
            if (clrType == null)
                throw new ArgumentNullException(nameof(clrType));
            if (_byClr.TryGetValue(clrType, out var md))
                return md;
            throw new InvalidOperationException($"VaultRegistry: CLR type '{clrType.FullName}' not registered.");
        }

        /// <summary>Keyspace of a registered model type.</summary>
        /// <param name="clrType">The model type.</param>
        /// <returns>The keyspace.</returns>
        public static string GetKeyspace(Type clrType) => GetByClr(clrType).Keyspace;
        /// <summary>CLR type for a type key (see <see cref="GetByTypeKey"/>).</summary>
        /// <param name="typeKey">Type key or unique simple name.</param>
        /// <returns>The model type.</returns>
        public static Type GetClr(string typeKey) => GetByTypeKey(typeKey).ClrType;
        /// <summary>Computes the type key (full CLR name) of any type; does not require registration.</summary>
        /// <param name="clrType">The type.</param>
        /// <returns>The type key.</returns>
        public static string GetTypeKey(Type clrType) => GetDefaultTypeKey(clrType);

        /// <summary>Associates a vault instance with a model type so <see cref="GetVault{TModel}"/> can return it. For provider implementations.</summary>
        /// <param name="modelClrType">The model type.</param>
        /// <param name="vaultInstance">An <see cref="IVault{TVaultModel}"/> for that type.</param>
        public static void RegisterVaultInstance(Type modelClrType, object vaultInstance)
        {
            if (modelClrType is null)
                throw new ArgumentNullException(nameof(modelClrType));
            if (vaultInstance is null)
                throw new ArgumentNullException(nameof(vaultInstance));
            _vaultByClr[modelClrType] = vaultInstance;
        }

        /// <summary>Registers a lookup-by-StorageId function used by <see cref="FindByStorageIdAsync"/>. For provider implementations.</summary>
        /// <param name="modelClrType">The model type.</param>
        /// <param name="finder">Returns the model with the given StorageId, or null.</param>
        public static void RegisterFindByIdDelegate(Type modelClrType, Func<string, Task<IVaultModel?>> finder)
        {
            if (modelClrType is null)
                throw new ArgumentNullException(nameof(modelClrType));
            if (finder is null)
                throw new ArgumentNullException(nameof(finder));
            _findById[modelClrType] = finder;
        }

        /// <summary>Returns the vault registered via <see cref="RegisterVaultInstance"/>. Prefer injecting <see cref="IVault{TVaultModel}"/>.</summary>
        /// <typeparam name="TModel">The model type.</typeparam>
        /// <returns>The vault.</returns>
        /// <exception cref="InvalidOperationException">No instance registered.</exception>
        public static IVault<TModel> GetVault<TModel>() where TModel : class, IVaultModel
        {
            if (_vaultByClr.TryGetValue(typeof(TModel), out var v))
                return (IVault<TModel>)v;
            throw new InvalidOperationException($"VaultRegistry: IVault<{typeof(TModel).FullName}> instance not registered.");
        }

        /// <summary>Non-generic <see cref="GetVault{TModel}"/>; returns the boxed vault instance.</summary>
        /// <param name="modelClrType">The model type.</param>
        /// <returns>The vault instance.</returns>
        /// <exception cref="InvalidOperationException">No instance registered.</exception>
        public static object GetVault(Type modelClrType)
        {
            if (modelClrType is null)
                throw new ArgumentNullException(nameof(modelClrType));
            if (_vaultByClr.TryGetValue(modelClrType, out var v))
                return v;
            throw new InvalidOperationException($"VaultRegistry: IVault<{modelClrType.FullName}> instance not registered.");
        }

        /// <summary>Loads a model by type and StorageId through the delegate registered with <see cref="RegisterFindByIdDelegate"/>.</summary>
        /// <param name="modelClrType">The model type.</param>
        /// <param name="storageId">The id.</param>
        /// <returns>The model or null.</returns>
        /// <exception cref="InvalidOperationException">No finder registered for the type.</exception>
        public static Task<IVaultModel?> FindByStorageIdAsync(Type modelClrType, string storageId)
        {
            if (modelClrType is null)
                throw new ArgumentNullException(nameof(modelClrType));
            if (storageId is null)
                throw new ArgumentNullException(nameof(storageId));
            if (_findById.TryGetValue(modelClrType, out var f))
                return f(storageId);
            throw new InvalidOperationException($"VaultRegistry: no finder registered for {modelClrType.FullName}.");
        }

        private static string GetDefaultTypeKey(Type clrType)
            => clrType.FullName ?? clrType.Name;
    }
}
