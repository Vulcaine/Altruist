// Document.cs
/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

using Altruist.UORM;

namespace Altruist.Persistence;

public sealed class VaultDocument
{
    // ----------------- Cache -----------------

    // Lazy prevents duplicate builds under concurrency and ensures only one builder runs.
    private static readonly ConcurrentDictionary<Type, Lazy<VaultDocument>> _cache = new();

    /// <summary>
    /// Clears the cached Documents. Useful for tests / hot reload scenarios.
    /// </summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>
    /// Removes a single type from the cache. Useful for tests.
    /// </summary>
    public static bool RemoveFromCache(Type type)
        => type is not null && _cache.TryRemove(type, out _);

    // ----------------- Instance -----------------

    public Type Type { get; }
    public VaultAttribute Header { get; }
    public string Name { get; }

    public bool StoreHistory { get; }
    public VaultPrimaryKeyAttribute? PrimaryKey { get; }
    public VaultSortingByAttribute? SortingBy { get; }

    // logical field names (CLR property names)
    public List<string> Fields { get; }
    // logical field name -> physical column name
    public Dictionary<string, string> Columns { get; }
    // physical column names
    public List<string> Indexes { get; }
    // physical UNIQUE constraints
    public List<UniqueKeyDefinition> UniqueKeys { get; }
    // FK definitions (dependent column name is physical)
    public List<VaultForeignKeyDefinition> ForeignKeys { get; }

    // logical field name -> CLR type
    public Dictionary<string, Type> FieldTypes { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // physical column names that are nullable
    public HashSet<string> NullableColumns { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // new physical column name -> list of old physical column names (from [VaultRenamedFrom], ordered oldest→newest)
    // The planner picks the first one that exists in the current DB schema.
    public Dictionary<string, List<string>> RenamedColumns { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // target physical column name -> source physical column name (from [VaultColumnCopy])
    public Dictionary<string, string> CopyFromColumns { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // physical column names marked for deletion (from [VaultColumnDelete]) -> reason
    public Dictionary<string, string> DeletedColumns { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // true if [VaultTableDelete] is on the class — entire table should be dropped
    public bool IsTableDeleted { get; internal set; }
    public string TableDeleteReason { get; internal set; } = "";

    // true if [VaultArchived] is on the class — data copied to archive table, then original dropped
    public bool IsTableArchived { get; internal set; }
    public string ArchiveTableName { get; internal set; } = "";
    public string ArchiveReason { get; internal set; } = "";

    // logical field name -> accessor compiled once
    public Dictionary<string, Func<object, object?>> PropertyAccessors { get; }

    // kept for compatibility (was in older version); not used in this refactor
    public string TypePropertyName { get; set; } = "";

    public VaultDocument(
        VaultAttribute header,
        Type type,
        string name,
        List<string> fields,
        Dictionary<string, string> columns,
        List<string> indexes,
        List<UniqueKeyDefinition> uniqueKeys,
        Dictionary<string, Func<object, object?>> propertyAccessors,
        VaultPrimaryKeyAttribute? primaryKeyAttribute = null,
        VaultSortingByAttribute? sortingByAttribute = null,
        bool storeHistory = false,
        List<VaultForeignKeyDefinition>? foreignKeys = null)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Name = name ?? throw new ArgumentNullException(nameof(name));

        Fields = fields ?? new();
        Columns = columns ?? new();
        Indexes = indexes ?? new();
        UniqueKeys = uniqueKeys ?? new();
        PropertyAccessors = propertyAccessors ?? new();

        PrimaryKey = primaryKeyAttribute;
        SortingBy = sortingByAttribute;
        StoreHistory = storeHistory;

        ForeignKeys = foreignKeys ?? new();

        Validate(); // Document is always validated on creation
    }

    /// <summary>
    /// The single entry point: builds a Document for any type that has a [Vault] (or derived) attribute.
    /// Cached per type.
    /// </summary>
    public static VaultDocument From(Type type)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));

        // ExecutionAndPublication: only one build runs, others wait and reuse the result.
        var lazy = _cache.GetOrAdd(
            type,
            static t => new Lazy<VaultDocument>(
                () => DocumentBuilder.Build(t),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    /// <summary>
    /// Convenience generic version.
    /// </summary>
    public static VaultDocument From<T>() => From(typeof(T));

    public void Validate() => DocumentValidator.Validate(this);

    public override string ToString() => $"{Type.Name} [{Name}]";

    public static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    internal static void FailAndExit(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(-1);
    }

    // ----------------- Definitions -----------------

    public sealed class UniqueKeyDefinition
    {
        /// <summary>Physical column names participating in this UNIQUE constraint.</summary>
        public IReadOnlyList<string> Columns { get; }

        public UniqueKeyDefinition(IEnumerable<string> columns)
        {
            if (columns is null)
                throw new ArgumentNullException(nameof(columns));

            var list = columns
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count == 0)
                throw new ArgumentException("Unique key must contain at least one column.", nameof(columns));

            Columns = list;
        }
    }

    public sealed class VaultForeignKeyDefinition
    {
        public string PropertyName { get; }
        public string ColumnName { get; }                 // dependent physical column name
        public Type PrincipalType { get; }
        public string PrincipalPropertyName { get; }
        public string OnDelete { get; }

        public VaultForeignKeyDefinition(
            string propertyName,
            string columnName,
            Type principalType,
            string principalPropertyName,
            string? onDelete = null)
        {
            PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
            ColumnName = columnName ?? throw new ArgumentNullException(nameof(columnName));
            PrincipalType = principalType ?? throw new ArgumentNullException(nameof(principalType));
            PrincipalPropertyName = principalPropertyName ?? throw new ArgumentNullException(nameof(principalPropertyName));
            OnDelete = string.IsNullOrWhiteSpace(onDelete) ? "CASCADE" : onDelete;
        }
    }

    public string QualifiedTable()
    {
        var schema = string.IsNullOrWhiteSpace(Header.Keyspace) ? "public" : Header.Keyspace.Trim();
        return $"{Quote(schema)}.{Quote(Name)}";
    }

    public static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    public string Col(string logical)
       => Columns.TryGetValue(logical, out var physical) ? physical : ToCamelCase(logical);
}
