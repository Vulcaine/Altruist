/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;
using System.Text.Json.Serialization;

using Altruist.Persistence;
using Altruist.UORM;

using StackExchange.Redis;

namespace Altruist.Redis;

/// <summary>Builds the <see cref="VaultDocument"/> mappings the Redis cache uses for key names and polymorphic reads.</summary>
public static class RedisDocumentHelper
{
    /// <summary>
    /// Creates one <see cref="VaultDocument"/> per type registered in <see cref="RedisServiceConfiguration.Documents"/>
    /// (of <see cref="RedisCacheServiceToken.Instance"/>). When the type has a <c>Type</c> property, sets
    /// <see cref="VaultDocument.TypePropertyName"/> to its <see cref="JsonPropertyNameAttribute"/> name (or <c>"Type"</c>),
    /// which <see cref="RedisCacheProvider"/> reads as the JSON type discriminator.
    /// </summary>
    /// <param name="mux">Unused; kept for signature compatibility.</param>
    /// <returns>The document list (empty when no types were registered).</returns>
    public static List<VaultDocument> CreateDocuments(IConnectionMultiplexer mux)
    {
        var config = (RedisCacheServiceToken.Instance.Configuration as RedisServiceConfiguration)!;
        var documents = new List<VaultDocument>();

        foreach (var docType in config.Documents)
        {
            var doc = VaultDocument.From(docType);

            // Resolve the TypePropertyName for polymorphic deserialization
            var typeProperty = docType.GetProperty("Type");
            if (typeProperty != null)
            {
                var jsonAttr = typeProperty.GetCustomAttribute<JsonPropertyNameAttribute>();
                doc.TypePropertyName = jsonAttr?.Name ?? "Type";
            }

            documents.Add(doc);
        }

        return documents;
    }
}
