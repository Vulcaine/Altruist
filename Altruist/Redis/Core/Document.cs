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

public static class RedisDocumentHelper
{
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
