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

            documents.Add(new RedisDocument(document, tableAttribute?.Name ?? document.Name, document.GetProperties().Select(prop => prop.Name).ToList(), indexedFields));
        }

        return documents;
    }
}

public class RedisDocument : Document
{
    public RedisDocument(Type type, string name, List<string> fields, List<string> indexes) : base(type, name, fields, indexes)
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
