/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist;

using System;

using Altruist.UORM;

/// <summary>
/// Alias for <see cref="VaultAttribute"/> tailored for prefabs.
/// Registers prefabs in the same pipeline as vault models: same keyspace, same table bootstrap.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PrefabAttribute : VaultAttribute
{
    /// <summary>
    /// Logical prefab id (kept for ergonomics). Mirrors <see cref="VaultAttribute.Name"/>.
    /// </summary>
    public string Id => base.Name;

    /// <summary>
    /// Prefab alias. Works identically to <see cref="VaultAttribute"/> but reads nicer on prefab classes.
    /// </summary>
    /// <param name="id">Prefab id (stored as <see cref="VaultAttribute.Name"/>).</param>
    /// <param name="StoreHistory">If true, enable historical storage.</param>
    /// <param name="Keyspace">DB keyspace; defaults to "altruist".</param>
    /// <param name="DbToken">DB token/provider; defaults to "ScyllaDB".</param>
    public PrefabAttribute(
        string id,
        bool StoreHistory = false,
        string Keyspace = "altruist",
        string DbToken = "ScyllaDB")
        : base(Name: id, StoreHistory: StoreHistory, Keyspace: Keyspace, DbToken: DbToken)
    {
    }
}
