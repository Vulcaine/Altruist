/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/
using System.Collections.Concurrent;

namespace Altruist
{
    public sealed class VaultMetadata
    {
        public string TypeKey { get; }
        public Type ClrType { get; }
        public string Keyspace { get; }

        public VaultMetadata(string typeKey, Type clrType, string keyspace)
        {
            TypeKey = typeKey ?? throw new ArgumentNullException(nameof(typeKey));
            ClrType = clrType ?? throw new ArgumentNullException(nameof(clrType));
            Keyspace = string.IsNullOrWhiteSpace(keyspace) ? "altruist" : keyspace;
        }
    }

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

        public static void Register<TModel>(string keyspace)
            where TModel : class, IVaultModel
            => Register(typeof(TModel), keyspace);

        public static IReadOnlyCollection<VaultMetadata> GetAll()
        {
            return _byClr.Values.Distinct().ToArray();
        }

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

        public static VaultMetadata GetByClr(Type clrType)
        {
            if (clrType == null)
                throw new ArgumentNullException(nameof(clrType));
            if (_byClr.TryGetValue(clrType, out var md))
                return md;
            throw new InvalidOperationException($"VaultRegistry: CLR type '{clrType.FullName}' not registered.");
        }

        public static string GetKeyspace(Type clrType) => GetByClr(clrType).Keyspace;
        public static Type GetClr(string typeKey) => GetByTypeKey(typeKey).ClrType;
        public static string GetTypeKey(Type clrType) => GetDefaultTypeKey(clrType);

        public static void RegisterVaultInstance(Type modelClrType, object vaultInstance)
        {
            if (modelClrType is null)
                throw new ArgumentNullException(nameof(modelClrType));
            if (vaultInstance is null)
                throw new ArgumentNullException(nameof(vaultInstance));
            _vaultByClr[modelClrType] = vaultInstance;
        }

        public static void RegisterFindByIdDelegate(Type modelClrType, Func<string, Task<IVaultModel?>> finder)
        {
            if (modelClrType is null)
                throw new ArgumentNullException(nameof(modelClrType));
            if (finder is null)
                throw new ArgumentNullException(nameof(finder));
            _findById[modelClrType] = finder;
        }

        public static IVault<TModel> GetVault<TModel>() where TModel : class, IVaultModel
        {
            if (_vaultByClr.TryGetValue(typeof(TModel), out var v))
                return (IVault<TModel>)v;
            throw new InvalidOperationException($"VaultRegistry: IVault<{typeof(TModel).FullName}> instance not registered.");
        }

        public static object GetVault(Type modelClrType)
        {
            if (modelClrType is null)
                throw new ArgumentNullException(nameof(modelClrType));
            if (_vaultByClr.TryGetValue(modelClrType, out var v))
                return v;
            throw new InvalidOperationException($"VaultRegistry: IVault<{modelClrType.FullName}> instance not registered.");
        }

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
