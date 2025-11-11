/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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

    /// <summary>
    /// Global registry for vault model metadata.
    /// Register once at startup with the CLR type of the model; no custom string keys required.
    /// 
    /// By default the <see cref="TypeKey"/> is the model's <c>FullName</c>. Store that value
    /// into <c>ModelRef.Type</c> for O(1) lookups on hot paths.
    /// </summary>
    public static class VaultRegistry
    {
        // Primary index: type key (we expect FullName) -> metadata
        private static readonly ConcurrentDictionary<string, VaultMetadata> _byTypeKey =
            new(StringComparer.Ordinal);

        // CLR type -> metadata
        private static readonly ConcurrentDictionary<Type, VaultMetadata> _byClr =
            new();

        // Optional: simple-name index to tolerate manifests that stored simple names.
        // Using StringComparer.Ordinal to keep it tight; collisions are handled.
        private static readonly ConcurrentDictionary<string, VaultMetadata> _bySimpleName =
            new(StringComparer.Ordinal);

        // Track simple-name collisions so we can fail fast if ambiguous.
        private static readonly ConcurrentDictionary<string, bool> _simpleNameCollision =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Register a model mapping (CLR type) and its keyspace.
        /// The type key used internally will be <c>typeof(TModel).FullName</c>.
        /// </summary>
        public static void Register<TModel>(string keyspace)
            where TModel : class, IVaultModel
            => Register(typeof(TModel), keyspace);

        /// <summary>
        /// Register a model mapping (CLR type) and its keyspace.
        /// The type key used internally will be <c>clrType.FullName</c>.
        /// </summary>
        public static void Register(Type clrType, string keyspace)
        {
            if (clrType is null) throw new ArgumentNullException(nameof(clrType));

            var typeKey = GetDefaultTypeKey(clrType);
            var md = new VaultMetadata(typeKey, clrType, keyspace);

            _byTypeKey[typeKey] = md;
            _byClr[clrType] = md;

            // Maintain simple-name index (best-effort), track collisions.
            var simple = clrType.Name;
            if (!_bySimpleName.TryAdd(simple, md))
            {
                // Collision: mark as ambiguous; subsequent GetByTypeKey(simple) will throw.
                _simpleNameCollision[simple] = true;
            }
        }

        /// <summary>
        /// Get metadata by stored type key (we expect FullName; simple name is tolerated if unique).
        /// </summary>
        public static VaultMetadata GetByTypeKey(string typeKey)
        {
            if (string.IsNullOrWhiteSpace(typeKey))
                throw new ArgumentException("Type key is required.", nameof(typeKey));

            // Fast path: exact match (FullName).
            if (_byTypeKey.TryGetValue(typeKey, out var md)) return md;

            // Tolerate simple-name manifests (non-alloc, O(1) average).
            if (_simpleNameCollision.ContainsKey(typeKey))
                throw new InvalidOperationException(
                    $"VaultRegistry: simple type name '{typeKey}' is ambiguous; store the full name instead.");

            if (_bySimpleName.TryGetValue(typeKey, out md)) return md;

            throw new InvalidOperationException($"VaultRegistry: type key '{typeKey}' not registered.");
        }

        /// <summary>Get metadata by CLR type.</summary>
        public static VaultMetadata GetByClr(Type clrType)
        {
            if (clrType == null) throw new ArgumentNullException(nameof(clrType));
            if (_byClr.TryGetValue(clrType, out var md)) return md;
            throw new InvalidOperationException($"VaultRegistry: CLR type '{clrType.FullName}' not registered.");
        }

        public static string GetKeyspace(Type clrType) => GetByClr(clrType).Keyspace;
        public static Type GetClr(string typeKey) => GetByTypeKey(typeKey).ClrType;
        public static string GetTypeKey(Type clrType) => GetDefaultTypeKey(clrType);

        private static string GetDefaultTypeKey(Type clrType)
            => clrType.FullName ?? clrType.Name;
    }
}
