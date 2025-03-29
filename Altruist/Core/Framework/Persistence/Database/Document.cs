using System.Linq.Expressions;
using System.Reflection;
using Altruist.UORM;

namespace Altruist.Database;


public class Document
{
    public Type Type { get; set; }
    public VaultAttribute Header { get; set; }
    public string Name { get; set; }

    public bool StoreHistory { get; set; }

    public VaultPrimaryKeyAttribute? PrimaryKey { get; set; } = new();

    public List<string> UniqueKeys { get; set; } = new();

    public VaultSortingByAttribute? SortingBy { get; set; }

    public List<string> Fields { get; set; } = new();     // logical field names (camelCase)
    public Dictionary<string, string> Columns { get; set; } = new();    // actual column names

    public List<string> Indexes { get; set; } = new();

    public string TypePropertyName;

    // Precompiled property accessors: PropertyName -> (object instance) => value
    public Dictionary<string, Func<object, object?>> PropertyAccessors { get; set; } = new();

    public Document(

        VaultAttribute header,
        Type type,
        string name,
        List<string> fields,
        Dictionary<string, string> columns,
        List<string> indexes,
        Dictionary<string, Func<object, object?>> propertyAccessors,
        VaultPrimaryKeyAttribute? primaryKeyAttribute = null,
        VaultSortingByAttribute? sortingByAttribute = null, bool storeHistory = false)
    {
        PrimaryKey = primaryKeyAttribute;
        Header = header;
        Type = type;
        Name = name;
        Fields = fields;
        Columns = columns;
        Indexes = indexes;
        PropertyAccessors = propertyAccessors;
        TypePropertyName = "";
        SortingBy = sortingByAttribute;
        StoreHistory = storeHistory;

        Validate();
    }

    public static Document From(Type type)
    {
        if (!typeof(IStoredModel).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"The type {type.FullName} must implement IModel.");
        }

        var vaultAttribute = type.GetCustomAttribute<VaultAttribute>();
        var name = vaultAttribute?.Name ?? type.Name;

        var fields = new List<string>();
        var columns = new Dictionary<string, string>();
        var indexes = new List<string>();
        var accessors = new Dictionary<string, Func<object, object?>>();
        var primaryKey = type.GetCustomAttribute<VaultPrimaryKeyAttribute>();
        var sortingBy = type.GetCustomAttribute<VaultSortingByAttribute>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var columnAttr = prop.GetCustomAttribute<VaultColumnAttribute>();
            var columnName = columnAttr?.Name ?? ToCamelCase(prop.Name);
            var fieldName = prop.Name;

            fields.Add(fieldName);
            columns[fieldName] = columnName;

            // compile and cache accessor
            accessors[fieldName] = CompileAccessor(prop);

            if (prop.GetCustomAttribute<VaultColumnIndexAttribute>() != null)
            {
                var indexName = columnAttr?.Name ?? ToCamelCase(prop.Name);
                indexes.Add(indexName);
            }
        }

        return new Document(
            vaultAttribute!, type, name, fields, columns, indexes, accessors, primaryKey, sortingBy, vaultAttribute == null ? false : vaultAttribute.StoreHistory);
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
            throw new InvalidOperationException($"The type {Type.FullName} must implement IModel.");
        }

        var typeProperty = Type.GetProperty("Type");
        if (typeProperty == null)
        {
            throw new InvalidOperationException($"The type {Type.FullName} must have a 'Type' property.");
        }
    }
}
