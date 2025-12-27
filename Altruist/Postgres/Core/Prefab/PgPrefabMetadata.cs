using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Altruist.Persistence.Postgres;

public interface IPgModelSqlMetadataProvider
{
    void RegisterModel<T>(PgVault<T> vault) where T : class, IVaultModel;

    PgModelSqlMetadata Get(Type modelType);
}

public sealed class PgModelSqlMetadata
{
    public required Type ModelType { get; init; }
    public required VaultDocument Document { get; init; }
    public required string QualifiedTable { get; init; }

    public required string PrimaryKeyProperty { get; init; } // e.g. "StorageId"
    public required string PrimaryKeyColumn { get; init; }   // mapped column

    public required string UpsertSql { get; init; }
    public required Func<object, List<object?>> GetUpsertParameters { get; init; }
}

[Service(typeof(IPgModelSqlMetadataProvider))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PgModelSqlMetadataProvider : IPgModelSqlMetadataProvider
{
    private readonly ConcurrentDictionary<Type, PgModelSqlMetadata> _byType = new();

    public void RegisterModel<T>(PgVault<T> vault) where T : class, IVaultModel
    {
        var type = typeof(T);

        // Already registered
        if (_byType.ContainsKey(type))
            return;

        var doc = vault.VaultDocument;

        var qualified =
            $"\"{vault.Keyspace.Name}\".\"{vault.VaultDocument.Name}\"";

        const string pkProp = nameof(IVaultModel.StorageId);

        if (!doc.Columns.TryGetValue(pkProp, out var pkCol))
            throw new InvalidOperationException(
                $"Document for {type.Name} must map primary key '{pkProp}' in doc.Columns.");

        // Build ordered list of columns + compiled getters (one-time reflection here)
        var getters = new List<Func<object, object?>>();
        var columns = new List<string>();

        // Build a property map once
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);

        foreach (var kv in doc.Columns)
        {
            var propName = kv.Key;
            var colName = kv.Value;

            if (!props.TryGetValue(propName, out var prop))
                continue;

            columns.Add(Quote(colName));
            getters.Add(CompileBoxedGetter(type, prop));
        }

        if (columns.Count == 0)
            throw new InvalidOperationException($"No mapped writable columns found for {type.Name}.");

        // Build upsert SQL once
        var values = string.Join(", ", Enumerable.Repeat("?", columns.Count));

        var setClauses = new List<string>();
        foreach (var kv in doc.Columns)
        {
            var propName = kv.Key;
            if (string.Equals(propName, pkProp, StringComparison.Ordinal))
                continue;

            if (!props.ContainsKey(propName))
                continue;

            var colName = kv.Value;
            setClauses.Add($"{Quote(colName)} = EXCLUDED.{Quote(colName)}");
        }

        var upsertSql =
            $"INSERT INTO {qualified} ({string.Join(", ", columns)}) " +
            $"VALUES ({values}) " +
            $"ON CONFLICT ({Quote(pkCol)}) DO UPDATE SET {string.Join(", ", setClauses)};";

        var meta = new PgModelSqlMetadata
        {
            ModelType = type,
            Document = doc,
            QualifiedTable = qualified,
            PrimaryKeyProperty = pkProp,
            PrimaryKeyColumn = pkCol,
            UpsertSql = upsertSql,
            GetUpsertParameters = model =>
            {
                var list = new List<object?>(getters.Count);
                for (int i = 0; i < getters.Count; i++)
                    list.Add(getters[i](model));
                return list;
            }
        };

        _byType[type] = meta;
    }

    public PgModelSqlMetadata Get(Type modelType)
    {
        if (_byType.TryGetValue(modelType, out var meta))
            return meta;

        throw new InvalidOperationException(
            $"No PgModelSqlMetadata registered for type '{modelType.Name}'. " +
            "Ensure PgVault<T> registers itself with IPgModelSqlMetadataProvider at startup.");
    }

    private static Func<object, object?> CompileBoxedGetter(Type modelType, PropertyInfo prop)
    {
        // (object o) => (object?)((T)o).Prop
        var obj = Expression.Parameter(typeof(object), "o");
        var cast = Expression.Convert(obj, modelType);
        var access = Expression.Property(cast, prop);
        var box = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(box, obj).Compile();
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
}
