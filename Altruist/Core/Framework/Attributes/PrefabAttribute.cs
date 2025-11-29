/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Persistence;

using System;

using Altruist.UORM;

/// <summary>
/// Alias for <see cref="VaultAttribute"/> tailored for prefabs.
/// Registers prefabs in the same pipeline as vault models: same keyspace, same table bootstrap.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class PrefabAttribute : VaultAttribute
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
        string DbToken = "Postgres")
        : base(Name: id, StoreHistory: StoreHistory, Keyspace: Keyspace, DbToken: DbToken)
    {
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class PrefabComponentAttribute : Attribute
{
    /// <summary>
    /// Name of the prefab component this component should auto-load after.
    /// For example: AutoLoadOn = nameof(Character).
    /// </summary>
    public string? AutoLoadOn { get; set; }

    /// <summary>
    /// Name of the relation key used to tie this component to the AutoLoadOn component.
    /// Required when AutoLoadOn is specified (enforced at metadata registration time).
    /// For now this is validated but not deeply used; it’s reserved for richer relations.
    /// </summary>
    public string? RelationKey { get; set; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class OnPrefabComponentLoadAttribute : Attribute
{
    /// <summary>
    /// Name of the prefab component property this method reacts to.
    /// For example: [OnPrefabComponentLoad(nameof(Character))]
    /// </summary>
    public string ComponentName { get; }

    public OnPrefabComponentLoadAttribute(string componentName)
    {
        ComponentName = componentName ?? throw new ArgumentNullException(nameof(componentName));
    }
}
