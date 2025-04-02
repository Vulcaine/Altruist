namespace Altruist.UORM;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class TableAttribute : Attribute
{
    public string Name { get; }
    public bool StoreHistory { get; }
    public TableAttribute(string Name, bool StoreHistory = false) => (this.Name, this.StoreHistory) = (Name, StoreHistory);
}

[AttributeUsage(AttributeTargets.Class)]
public class PrimaryKeyAttribute : Attribute
{
    public string[] Keys { get; }
    public PrimaryKeyAttribute(params string[] keys) => Keys = keys;
}

[AttributeUsage(AttributeTargets.Property)]
public class ColumnIndexAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property)]
public class IgnoreAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class)]
public class SortingByAttribute : Attribute
{
    public string Name { get; }
    public bool Ascending { get; }
    public SortingByAttribute(
        string name,
        bool ascending = false) => (Name, Ascending) = (name, ascending);
}

[AttributeUsage(AttributeTargets.Property)]
public class ColumnAttribute : Attribute
{
    public string? Name { get; }
    public ColumnAttribute(string? name = null) => Name = name;
}
