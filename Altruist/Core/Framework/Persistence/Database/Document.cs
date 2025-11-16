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

using System.Linq.Expressions;
using System.Reflection;

using Altruist.UORM;

using Microsoft.Extensions.Logging;

namespace Altruist.Persistence;


public class Document
{
    public Type Type { get; set; }
    public VaultAttribute Header { get; set; }
    public string Name { get; set; }

    public bool StoreHistory { get; set; }

    public VaultPrimaryKeyAttribute? PrimaryKey { get; set; } = new();

    public List<string> UniqueKeys { get; set; } = new();

    public VaultSortingByAttribute? SortingBy { get; set; }

    public List<string> Fields { get; set; } = new();
    public Dictionary<string, string> Columns { get; set; } = new();

    public List<string> Indexes { get; set; } = new();

    public List<VaultForeignKeyDefinition> ForeignKeys { get; set; } = new();


    public string TypePropertyName;

    private readonly ILogger<Document>? _logger;

    // Precompiled property accessors: PropertyName -> (object instance) => value
    public Dictionary<string, Func<object, object?>> PropertyAccessors { get; set; } = new();

    public Document(
    VaultAttribute header,
    Type type,
    string name,
    List<string> fields,
    Dictionary<string, string> columns,
    List<string> indexes,
    List<string> uniqueKeys,
    Dictionary<string, Func<object, object?>> propertyAccessors,
    VaultPrimaryKeyAttribute? primaryKeyAttribute = null,
    VaultSortingByAttribute? sortingByAttribute = null,
    bool storeHistory = false,
    ILoggerFactory? loggerFactory = null,
    List<VaultForeignKeyDefinition>? foreignKeys = null
    )
    {
        PrimaryKey = primaryKeyAttribute;
        Header = header;
        Type = type;
        Name = name;
        Fields = fields;
        Columns = columns;
        Indexes = indexes;
        UniqueKeys = uniqueKeys;
        PropertyAccessors = propertyAccessors;
        TypePropertyName = "";
        SortingBy = sortingByAttribute;
        StoreHistory = storeHistory;
        ForeignKeys = foreignKeys ?? new List<VaultForeignKeyDefinition>();

        _logger = loggerFactory?.CreateLogger<Document>();
        Validate();
    }

    public static Document From(Type type, ILoggerFactory? loggerFactory = null)
    {
        if (!typeof(IStoredModel).IsAssignableFrom(type))
            throw new InvalidOperationException($"The type {type.FullName} must implement IModel.");

        var vaultAttribute = type.GetCustomAttribute<VaultAttribute>();
        var name = vaultAttribute?.Name ?? type.Name;

        var fields = new List<string>();
        var columns = new Dictionary<string, string>();
        var indexes = new List<string>();
        var uniqueKeys = new List<string>();
        var accessors = new Dictionary<string, Func<object, object?>>();
        var primaryKey = type.GetCustomAttribute<VaultPrimaryKeyAttribute>();
        var sortingBy = type.GetCustomAttribute<VaultSortingByAttribute>();
        var foreignKeys = new List<VaultForeignKeyDefinition>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var columnAttr = prop.GetCustomAttribute<VaultColumnAttribute>();
            var fkAttr = prop.GetCustomAttribute<VaultForeignKeyAttribute>();
            var physical = columnAttr?.Name ?? prop.Name.ToLowerInvariant();
            var fieldName = prop.Name;

            fields.Add(fieldName);
            columns[fieldName] = physical;

            // accessor cache
            accessors[fieldName] = CompileAccessor(prop);

            if (fkAttr is not null)
            {
                foreignKeys.Add(new VaultForeignKeyDefinition(
                    propertyName: fieldName,
                    columnName: physical,
                    principalType: fkAttr.PrincipalType,
                    principalPropertyName: fkAttr.PrincipalPropertyName,
                    onDelete: fkAttr.OnDelete));
            }

            // [VaultColumnIndex] -> non-unique index on physical column
            if (prop.GetCustomAttribute<VaultColumnIndexAttribute>() != null)
            {
                indexes.Add(physical);
            }

            // [VaultUniqueColumn] -> UNIQUE constraint on physical column
            if (prop.GetCustomAttribute<VaultUniqueColumnAttribute>() != null)
            {
                uniqueKeys.Add(physical);
            }
        }

        return new Document(
            vaultAttribute!,
            type,
            name,
            fields,
            columns,
            indexes,
            uniqueKeys,
            accessors,
            primaryKey,
            sortingBy,
            vaultAttribute != null && vaultAttribute.StoreHistory,
            loggerFactory,
            foreignKeys);
    }

    private static Func<object, object?> CompileAccessor(PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");

        var castInstance = Expression.Convert(instance, property.DeclaringType!);
        var propertyAccess = Expression.Property(castInstance, property);
        var castResult = Expression.Convert(propertyAccess, typeof(object));

        var lambda = Expression.Lambda<Func<object, object?>>(castResult, instance);
        return lambda.Compile();
    }

    public static string ToCamelCase(string value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : char.ToLower(value[0]) + value.Substring(1);
    }

    public virtual void Validate()
    {
        if (!typeof(IStoredModel).IsAssignableFrom(Type))
        {
            FailAndExit($"The type {Type.FullName} must implement IModel.");
        }

        var typeProperty = Type.GetProperty("Type");
        if (typeProperty == null)
        {
            FailAndExit($"The type {Type.FullName} must have a 'Type' property.");
        }

        // De-duplicate unique keys and indexes (case-insensitive)
        UniqueKeys = UniqueKeys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Indexes = Indexes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A physical column should not be both UNIQUE and separately indexed.
        // UNIQUE implies an index; we treat it as UNIQUE and drop the redundant index.
        var overlap = Indexes
            .Intersect(UniqueKeys, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var col in overlap)
        {
            _logger?.LogWarning(
                $"In vault '{Name}', column '{col}' " +
                "is marked with both VaultUniqueColumn and VaultColumnIndex. " +
                "Unique implies index; dropping the redundant non-unique index."
            );

            Indexes.RemoveAll(x => string.Equals(x, col, StringComparison.OrdinalIgnoreCase));
        }

        // -----------------------------
        // Foreign key validation
        // -----------------------------
        if (ForeignKeys is null || ForeignKeys.Count == 0)
            return;

        // allowed ON DELETE behaviors
        static bool IsValidOnDelete(string value) =>
            value.Equals("CASCADE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("RESTRICT", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("SET NULL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("SET DEFAULT", StringComparison.OrdinalIgnoreCase);

        foreach (var fk in ForeignKeys)
        {
            // 1) Validate OnDelete value
            if (!IsValidOnDelete(fk.OnDelete))
            {
                FailAndExit(
                    $"Invalid OnDelete value '{fk.OnDelete}' on foreign key " +
                    $"'{Type.FullName}.{fk.PropertyName}'. " +
                    "Allowed values are: CASCADE, NO ACTION, RESTRICT, SET NULL, SET DEFAULT.");
            }

            var principalType = fk.PrincipalType;

            // 2) Principal type must be a stored model with [Vault]
            if (!typeof(IStoredModel).IsAssignableFrom(principalType))
            {
                FailAndExit(
                    $"Foreign key '{Type.FullName}.{fk.PropertyName}' points to type '{principalType.FullName}' " +
                    "which does not implement IStoredModel.");
            }

            var principalVaultAttr = principalType.GetCustomAttribute<VaultAttribute>();
            if (principalVaultAttr is null)
            {
                FailAndExit(
                    $"Foreign key '{Type.FullName}.{fk.PropertyName}' points to type '{principalType.FullName}' " +
                    "which is missing [Vault] attribute.");
            }

            // 3) Principal property must exist
            var principalProp = principalType.GetProperty(
                fk.PrincipalPropertyName,
                BindingFlags.Public | BindingFlags.Instance);

            if (principalProp is null)
            {
                FailAndExit(
                    $"Foreign key '{Type.FullName}.{fk.PropertyName}' points to " +
                    $"'{principalType.FullName}.{fk.PrincipalPropertyName}', " +
                    "but that property does not exist.");
            }

            // 4) Principal property must be PK or UNIQUE
            var principalPkAttr = principalType.GetCustomAttribute<VaultPrimaryKeyAttribute>();
            bool isPk = principalPkAttr?.Keys
                .Any(k => string.Equals(k, fk.PrincipalPropertyName, StringComparison.OrdinalIgnoreCase)) == true;

            bool isUnique = principalProp?.GetCustomAttribute<VaultUniqueColumnAttribute>() is not null;

            if (!isPk && !isUnique)
            {
                FailAndExit(
                    $"Foreign key '{Type.FullName}.{fk.PropertyName}' points to " +
                    $"'{principalType.FullName}.{fk.PrincipalPropertyName}', " +
                    "but that property is neither part of [VaultPrimaryKey] nor marked [VaultUniqueColumn]. " +
                    "PostgreSQL requires referenced columns to be PRIMARY KEY or UNIQUE.");
            }
        }
    }

    private void FailAndExit(string message)
    {
        _logger?.LogError(message);
        Environment.Exit(-1);
    }

    public sealed class VaultForeignKeyDefinition
    {
        public string PropertyName { get; }
        public string ColumnName { get; }
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

}
