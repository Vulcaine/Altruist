using System.Reflection;
using System.Text.Json.Serialization;
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

            documents.Add(new RedisDocument(document, tableAttribute?.Name ?? document.Name, indexedFields));
        }

        return documents;
    }
}

public class RedisDocument
{
    public Type Type { get; set; }
    public string Name { get; set; }
    public List<string> Indexes { get; set; } = new();

    public string TypePropertyName;

    public RedisDocument(Type type, string name, List<string> indexes)
    {
        Type = type;
        Name = name;
        Indexes = indexes;
        TypePropertyName = "";
        Validate();
    }

    public void Validate()
    {
        if (!typeof(IModel).IsAssignableFrom(Type))
        {
            throw new InvalidOperationException($"The type {Type.FullName} must implement IModel.");
        }

        var typeProperty = Type.GetProperty("Type");
        if (typeProperty == null)
        {
            throw new InvalidOperationException($"The type {Type.FullName} must have a 'Type' property.");
        }

        var jsonPropertyNameAttr = typeProperty.GetCustomAttribute<JsonPropertyNameAttribute>();
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
