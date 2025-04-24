/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Reflection;
using System.Text.Json.Serialization;
using Altruist.Database;
using Altruist.UORM;
using StackExchange.Redis;

namespace Altruist.Redis;

public class RedisDocumentHelper
{
    private readonly IDatabase _redis;
    private readonly IConnectionMultiplexer _mux;

    private readonly RedisServiceConfiguration _config;
    public RedisDocumentHelper(IConnectionMultiplexer mux)
    {
        _redis = mux.GetDatabase();
        _mux = mux;
        _config = (RedisCacheServiceToken.Instance.Configuration as RedisServiceConfiguration)!;
    }

    public List<RedisDocument> CreateDocuments()
    {
        var documents = new List<RedisDocument>();

        foreach (var document in _config.Documents)
        {
            var tableAttribute = document.GetCustomAttribute<VaultAttribute>();
            var indexedFields = document
                .GetProperties()
                .Where(prop => prop.GetCustomAttribute<VaultColumnIndexAttribute>() != null)
                .Select(prop => prop.Name)
                .ToList();
            var doc = Document.From(document);

            documents.Add(new RedisDocument(
                doc.Header,
                doc.Type, doc.Name, doc.Fields, doc.Columns, doc.Indexes, doc.PropertyAccessors));
        }

        return documents;
    }
}

public class RedisDocument : Document
{
    public RedisDocument(
        VaultAttribute header, Type type, string name, List<string> fields, Dictionary<string, string> columns, List<string> indexes, Dictionary<string, Func<object, object?>> propertyAccessors, VaultPrimaryKeyAttribute? primaryKeyAttribute = null, VaultSortingByAttribute? vaultSortingByAttribute = null) : base(header, type, name, fields, columns, indexes, propertyAccessors, primaryKeyAttribute, vaultSortingByAttribute)
    {
    }

    public override void Validate()
    {
        base.Validate();

        var typeProperty = Type.GetProperty("Type");
        var jsonPropertyNameAttr = typeProperty!.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (jsonPropertyNameAttr != null)
        {
            TypePropertyName = jsonPropertyNameAttr.Name;
        }
        else
        {
            TypePropertyName = "Type";
        }
    }
}
