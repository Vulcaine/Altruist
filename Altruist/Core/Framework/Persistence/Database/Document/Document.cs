// Document.cs
/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

using Altruist.UORM;

namespace Altruist.Persistence;

/// <summary>
/// Table metadata for a vault model, built by reflection from its <c>[Vault]</c>, <c>[VaultColumn]</c>, key, index,
/// foreign-key and migration attributes: table name, logical-to-physical column map, keys, history flag and compiled
/// property accessors.
/// </summary>
/// <remarks>
/// <para>
/// Get one with <see cref="From{T}"/> / <see cref="From(Type)"/>; results are cached per type and thread-safe. Used by
/// vaults, query translators and the schema migrator. Useful in application code mainly to resolve physical column
/// names (<see cref="Col"/>) or the qualified table name (<see cref="QualifiedTable"/>) when writing raw SQL.
/// </para>
/// <para>
/// Naming: a <c>[Vault]</c> with an empty name maps to the snake_case type name; a <c>[VaultColumn]</c> without a name
/// maps to the snake_case property name. Only properties with <c>[VaultColumn]</c> (and without <c>[VaultIgnore]</c> or
/// <c>[VaultColumnDelete]</c>) become columns.
/// </para>
/// <para>
/// Validation problems (missing <c>Type</c> property, invalid foreign keys) throw an
/// <see cref="InvalidVaultModelException"/> listing every problem; they are model-definition bugs to fix at startup.
/// Foreign keys may reference the model itself or form cycles between models.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var doc = VaultDocument.From&lt;PlayerProfile&gt;();
/// var sql = $"SELECT COUNT(*) FROM {doc.QualifiedTable()} WHERE {VaultDocument.Quote(doc.Col(nameof(PlayerProfile.Level)))} &gt; ?";
/// </code>
/// </example>
public sealed class VaultDocument
{
    // ----------------- Cache -----------------

    // Lazy prevents duplicate builds under concurrency and ensures only one builder runs.
    private static readonly ConcurrentDictionary<Type, Lazy<VaultDocument>> _cache = new();

    // Types whose foreign keys were checked against their principals (done outside the build, so that a model can
    // reference itself or another model that references it back).
    private static readonly ConcurrentDictionary<Type, bool> _foreignKeysValidated = new();

    /// <summary>
    /// Clears the cached Documents. Useful for tests / hot reload scenarios.
    /// </summary>
    public static void ClearCache()
    {
        _cache.Clear();
        _foreignKeysValidated.Clear();
    }

    /// <summary>
    /// Removes a single type from the cache. Useful for tests.
    /// </summary>
    public static bool RemoveFromCache(Type type)
    {
        if (type is null)
            return false;
        _foreignKeysValidated.TryRemove(type, out _);
        return _cache.TryRemove(type, out _);
    }

    // ----------------- Instance -----------------

    /// <summary>The vault model CLR type.</summary>
    public Type Type { get; }
    /// <summary>The model's <see cref="VaultAttribute"/> (or derived attribute), carrying keyspace, history and instance settings.</summary>
    public VaultAttribute Header { get; }
    /// <summary>Physical table name (unquoted, without schema).</summary>
    public string Name { get; }

    /// <summary>Schema (keyspace) of the table; see <see cref="VaultAttribute.SchemaName"/>.</summary>
    public string SchemaName => Header.SchemaName;

    /// <summary>Whether saves may append snapshots to the <c>&lt;table&gt;_history</c> table (from <see cref="VaultAttribute.StoreHistory"/>).</summary>
    public bool StoreHistory { get; }
    /// <summary>The primary-key declaration (<see cref="VaultModel"/> declares <c>StorageId</c>), or <c>null</c>.</summary>
    public VaultPrimaryKeyAttribute? PrimaryKey { get; }
    /// <summary>The default sort declaration, or <c>null</c>.</summary>
    public VaultSortingByAttribute? SortingBy { get; }

    // logical field names (CLR property names)
    /// <summary>Logical field names (CLR property names) of all mapped columns, in reflection order.</summary>
    public List<string> Fields { get; }
    // logical field name -> physical column name
    /// <summary>Logical field name (CLR property name) to physical column name.</summary>
    public Dictionary<string, string> Columns { get; }
    // physical column names
    /// <summary>Physical column names with a non-unique index (<c>[VaultColumnIndex]</c>); columns already covered by a single-column unique key are removed.</summary>
    public List<string> Indexes { get; }
    // physical UNIQUE constraints
    /// <summary>UNIQUE constraints (<c>[VaultUniqueKey]</c>), de-duplicated. The first one is used as the upsert conflict target instead of the primary key.</summary>
    public List<UniqueKeyDefinition> UniqueKeys { get; }
    // FK definitions (dependent column name is physical)
    /// <summary>Foreign keys declared with <c>[VaultForeignKey]</c>.</summary>
    public List<VaultForeignKeyDefinition> ForeignKeys { get; }

    // logical field name -> CLR type
    /// <summary>Logical field name to CLR property type.</summary>
    public Dictionary<string, Type> FieldTypes { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // physical column names that are nullable
    /// <summary>Physical column names declared nullable (<c>[VaultColumn(nullable: true)]</c>).</summary>
    public HashSet<string> NullableColumns { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // physical column name -> CLR default value from the vault model initializer
    /// <summary>Physical column name to the value a freshly constructed model has for it (used as the column default by migrations). Empty when the type can't be instantiated.</summary>
    public Dictionary<string, object?> ColumnDefaultValues { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // new physical column name -> list of old physical column names (from [VaultRenamedFrom], ordered oldest→newest)
    // The planner picks the first one that exists in the current DB schema.
    /// <summary>New physical column name to its former names from <c>[VaultRenamedFrom]</c>, in declaration order; the migrator renames the first one found in the database.</summary>
    public Dictionary<string, List<string>> RenamedColumns { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // target physical column name -> source physical column name (from [VaultColumnCopy])
    /// <summary>Target physical column to source column from <c>[VaultColumnCopy]</c> (data copied during migration).</summary>
    public Dictionary<string, string> CopyFromColumns { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // physical column names marked for deletion (from [VaultColumnDelete]) -> reason
    /// <summary>Physical columns marked with <c>[VaultColumnDelete]</c> (dropped by migration) to the stated reason.</summary>
    public Dictionary<string, string> DeletedColumns { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    // true if [VaultTableDelete] is on the class — entire table should be dropped
    /// <summary><c>true</c> when the class has <c>[VaultTableDelete]</c>: migration drops the table.</summary>
    public bool IsTableDeleted { get; internal set; }
    /// <summary>Reason given in <c>[VaultTableDelete]</c>.</summary>
    public string TableDeleteReason { get; internal set; } = "";

    // true if [VaultArchived] is on the class — data copied to archive table, then original dropped
    /// <summary><c>true</c> when the class has <c>[VaultArchived]</c>: migration copies the rows to <see cref="ArchiveTableName"/>, then drops the table.</summary>
    public bool IsTableArchived { get; internal set; }
    /// <summary>Archive table name from <c>[VaultArchived]</c>.</summary>
    public string ArchiveTableName { get; internal set; } = "";
    /// <summary>Reason given in <c>[VaultArchived]</c>.</summary>
    public string ArchiveReason { get; internal set; } = "";

    // logical field name -> accessor compiled once
    /// <summary>Logical field name to a compiled getter (<c>instance =&gt; value</c>), for fast reflection-free reads.</summary>
    public Dictionary<string, Func<object, object?>> PropertyAccessors { get; }

    // kept for compatibility (was in older version); not used in this refactor
    /// <summary>Unused; kept for compatibility.</summary>
    public string TypePropertyName { get; set; } = "";

    /// <summary>
    /// Creates a document and validates the model itself (foreign keys are checked by <see cref="Validate"/> and by
    /// <see cref="From(Type)"/>). Normally built for you by <see cref="From(Type)"/>; construct one by hand only in
    /// tests or tooling.
    /// </summary>
    /// <param name="header">The model's vault attribute.</param>
    /// <param name="type">The model type.</param>
    /// <param name="name">Physical table name.</param>
    /// <param name="fields">Logical field names.</param>
    /// <param name="columns">Logical-to-physical column map.</param>
    /// <param name="indexes">Indexed physical columns.</param>
    /// <param name="uniqueKeys">UNIQUE constraints.</param>
    /// <param name="propertyAccessors">Compiled getters by logical name.</param>
    /// <param name="primaryKeyAttribute">Primary-key declaration, or <c>null</c>.</param>
    /// <param name="sortingByAttribute">Default sort declaration, or <c>null</c>.</param>
    /// <param name="storeHistory">Whether history is enabled.</param>
    /// <param name="foreignKeys">Foreign keys, or <c>null</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="header"/>, <paramref name="type"/> or <paramref name="name"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidVaultModelException">The model is invalid.</exception>
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

        DocumentValidator.ValidateModel(this);
    }

    /// <summary>
    /// The <see cref="Name"/> a <c>[Vault]</c> type's document has (the attribute's name, or the snake_case class
    /// name), without building or validating the full mapping. Use it where only the name matters (cache key
    /// prefixes); use <see cref="From(Type)"/> for the mapping itself.
    /// </summary>
    /// <param name="type">A type with a <c>[Vault]</c> attribute.</param>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> has no <c>[Vault]</c> attribute.</exception>
    public static string NameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return DocumentBuilder.TableNameOf(type);
    }

    /// <summary>
    /// The single entry point: builds a Document for any type that has a [Vault] (or derived) attribute, validated
    /// including its foreign keys. Cached per type.
    /// </summary>
    /// <exception cref="InvalidVaultModelException">The model or one of its foreign keys is invalid.</exception>
    public static VaultDocument From(Type type)
    {
        var doc = Structure(type);
        if (!_foreignKeysValidated.ContainsKey(type))
        {
            DocumentValidator.ValidateForeignKeys(doc, Structure);
            _foreignKeysValidated.TryAdd(type, true);
        }
        return doc;
    }

    /// <summary>The cached document, built and validated without checking its foreign keys.</summary>
    private static VaultDocument Structure(Type type)
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

    /// <summary>
    /// Validates the model definition (stored-model contract, <c>Type</c> property, foreign keys against their principal
    /// documents) and normalizes unique keys and indexes. <see cref="From(Type)"/> does this for you.
    /// </summary>
    /// <exception cref="InvalidVaultModelException">The definition is invalid; lists every problem.</exception>
    public void Validate()
    {
        DocumentValidator.ValidateModel(this);
        DocumentValidator.ValidateForeignKeys(this, Structure);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Type.Name} [{Name}]";

    /// <summary>Lower-cases the first character (<c>"PlayerId"</c> to <c>"playerId"</c>).</summary>
    /// <param name="value">The name to convert.</param>
    public static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    // ----------------- Definitions -----------------

    /// <summary>A UNIQUE constraint over one or more physical columns.</summary>
    public sealed class UniqueKeyDefinition
    {
        /// <summary>Physical column names participating in this UNIQUE constraint.</summary>
        public IReadOnlyList<string> Columns { get; }

        /// <summary>Creates the definition; blank and duplicate (case-insensitive) names are dropped.</summary>
        /// <param name="columns">Physical column names.</param>
        /// <exception cref="ArgumentException">No valid column remains.</exception>
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

    /// <summary>A foreign key from a dependent column to a principal model's primary-key or single-column unique column.</summary>
    public sealed class VaultForeignKeyDefinition
    {
        /// <summary>Dependent CLR property name.</summary>
        public string PropertyName { get; }
        /// <summary>Dependent physical column name.</summary>
        public string ColumnName { get; }                 // dependent physical column name
        /// <summary>The referenced (principal) vault model type.</summary>
        public Type PrincipalType { get; }
        /// <summary>Referenced property (or physical column) on the principal model.</summary>
        public string PrincipalPropertyName { get; }
        /// <summary>SQL <c>ON DELETE</c> action: <c>CASCADE</c> (default), <c>NO ACTION</c>, <c>RESTRICT</c>, <c>SET NULL</c> or <c>SET DEFAULT</c>.</summary>
        public string OnDelete { get; }

        /// <summary>Creates the definition.</summary>
        /// <param name="propertyName">Dependent CLR property name.</param>
        /// <param name="columnName">Dependent physical column name.</param>
        /// <param name="principalType">Referenced model type.</param>
        /// <param name="principalPropertyName">Referenced property or column.</param>
        /// <param name="onDelete"><c>ON DELETE</c> action; <c>null</c> or blank means <c>CASCADE</c>.</param>
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

    /// <summary>Returns the quoted, schema-qualified table name, e.g. <c>"altruist"."player_profile"</c> (schema: <see cref="SchemaName"/>).</summary>
    public string QualifiedTable() => $"{Quote(SchemaName)}.{Quote(Name)}";

    /// <summary>Quotes an SQL identifier with double quotes, escaping embedded quotes.</summary>
    /// <param name="s">The identifier.</param>
    public static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// Returns the physical column name of a mapped property (CLR property name, e.g. <c>nameof(Model.Level)</c>), or
    /// the name itself when it already is one of the physical column names. Not quoted; wrap with <see cref="Quote"/>.
    /// </summary>
    /// <param name="logical">CLR property name, or a physical column name.</param>
    /// <exception cref="ArgumentException">Neither a mapped property nor a column of this table.</exception>
    public string Col(string logical)
    {
        if (Columns.TryGetValue(logical, out var physical))
            return physical;
        foreach (var column in Columns.Values)
        {
            if (string.Equals(column, logical, StringComparison.Ordinal))
                return column;
        }
        throw new ArgumentException($"'{logical}' is neither a mapped [VaultColumn] property nor a column of {Type.Name} ({Name}).", nameof(logical));
    }
}
