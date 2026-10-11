/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;
using System.Text.Json.Serialization;

using Altruist.Persistence;


namespace Altruist.Redis;

/// <summary>
/// The Redis cache's mapping of one model type: the key prefix (<paramref name="Name"/>, the type's
/// <see cref="VaultDocument.NameOf"/>) and the JSON property that names a stored value's concrete type
/// (<paramref name="TypePropertyName"/>, empty when the type has no <c>Type</c> property).
/// </summary>
/// <param name="Type">The model type.</param>
/// <param name="Name">Key prefix: <c>{Name}:{key}</c> or <c>{Name}_{group}:{key}</c>.</param>
/// <param name="TypePropertyName">JSON name of the type-discriminator property, or empty.</param>
public sealed record RedisDocument(Type Type, string Name, string TypePropertyName);

/// <summary>Builds the <see cref="RedisDocument"/> mappings the Redis cache uses for key names and polymorphic reads.</summary>
public static class RedisDocumentHelper
{
    /// <summary>
    /// Discovers the document types (<see cref="RedisServiceConfiguration.DiscoverDocuments"/>) and creates one
    /// <see cref="RedisDocument"/> per type in <see cref="RedisServiceConfiguration.Documents"/>
    /// (of <see cref="RedisCacheServiceToken.Instance"/>). When the type has a <c>Type</c> property, the
    /// discriminator is its <see cref="JsonPropertyNameAttribute"/> name (or <c>"Type"</c>), which
    /// <see cref="RedisCacheProvider"/> reads to deserialize a stored subtype. Only the <c>[Vault]</c> name is
    /// read, so a model whose database mapping is invalid still gets a cache mapping.
    /// </summary>
    /// <returns>The documents (empty when no stored model types exist).</returns>
    public static IReadOnlyList<RedisDocument> CreateDocuments()
    {
        var config = (RedisServiceConfiguration)RedisCacheServiceToken.Instance.Configuration;
        config.DiscoverDocuments();
        return config.Documents.Select(Create).ToList();
    }

    private static RedisDocument Create(Type type)
    {
        var typeProperty = type.GetProperty("Type");
        var discriminator = typeProperty is null
            ? ""
            : typeProperty.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? "Type";
        return new RedisDocument(type, VaultDocument.NameOf(type), discriminator);
    }
}
