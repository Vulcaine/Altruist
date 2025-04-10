using System.Linq.Expressions;
using System.Reflection;
using Altruist.UORM;

namespace Altruist.Database;

public class Document
{
    public Type Type { get; set; }
    public VaultAttribute Header { get; set; }
    public string Name { get; set; }

    public List<string> Fields { get; set; } = new();     // logical field names (camelCase)
    public List<string> Columns { get; set; } = new();    // actual column names

    public List<string> Indexes { get; set; } = new();

    public string TypePropertyName;

    // Precompiled property accessors: PropertyName -> (object instance) => value
    public Dictionary<string, Func<object, object?>> PropertyAccessors { get; set; } = new();

    public Document(
        VaultAttribute header,
        Type type,
        string name,
        List<string> fields,
        List<string> columns,
        List<string> indexes,
        Dictionary<string, Func<object, object?>> propertyAccessors)
    {
        Header = header;
        Type = type;
        Name = name;
        Fields = fields;
        Columns = columns;
        Indexes = indexes;
        PropertyAccessors = propertyAccessors;
        TypePropertyName = "";

        Validate();
    }

    public static Document From(Type type)
    {
        if (!typeof(IModel).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"The type {type.FullName} must implement IModel.");
        }

        var vaultAttribute = type.GetCustomAttribute<VaultAttribute>();
        var name = vaultAttribute?.Name ?? type.Name;

        var fields = new List<string>();
        var columns = new List<string>();
        var indexes = new List<string>();
        var accessors = new Dictionary<string, Func<object, object?>>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var columnAttr = prop.GetCustomAttribute<VaultColumnAttribute>();
            if (columnAttr != null)
            {
                var columnName = columnAttr.Name ?? ToCamelCase(prop.Name);
                var fieldName = ToCamelCase(prop.Name);

                fields.Add(fieldName);
                columns.Add(columnName);

                // compile and cache accessor
                accessors[fieldName] = CompileAccessor(prop);
            }

            if (prop.GetCustomAttribute<VaultColumnIndexAttribute>() != null)
            {
                var indexName = columnAttr?.Name ?? ToCamelCase(prop.Name);
                indexes.Add(indexName);
            }
        }

        return new Document(vaultAttribute!, type, name, fields, columns, indexes, accessors);
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

    private static string ToCamelCase(string value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : char.ToLower(value[0]) + value.Substring(1);
    }

    public virtual void Validate()
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
    }
}
