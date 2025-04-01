using System.Reflection;
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
            var tableAttribute = document.GetCustomAttribute<TableAttribute>();
            var indexedFields = document
                .GetProperties()
                .Where(prop => prop.GetCustomAttribute<ColumnIndexAttribute>() != null)
                .Select(prop => prop.Name)
                .ToList();

            documents.Add(new RedisDocument()
            {
                Type = document,
                Name = tableAttribute?.Name ?? document.Name,
                Indexes = indexedFields
            });
        }

        return documents;
    }
}

public class RedisDocument
{
    public Type Type { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public List<string> Indexes { get; set; } = new();
}