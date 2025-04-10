namespace Altruist.UORM;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class VaultAttribute : Attribute
{
    public string Name { get; }
    public bool StoreHistory { get; }
    public VaultAttribute(string Name, bool StoreHistory = false) => (this.Name, this.StoreHistory) = (Name, StoreHistory);
}

[AttributeUsage(AttributeTargets.Class)]
public class VaultPrimaryKeyAttribute : Attribute
{
    public string[] Keys { get; }
    public VaultPrimaryKeyAttribute(params string[] keys) => Keys = keys;
}

[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnIndexAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property)]
public class VaultIgnoredAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class)]
public class VaultSortingByAttribute : Attribute
{
    public string Name { get; }
    public bool Ascending { get; }
    public VaultSortingByAttribute(
        string name,
        bool ascending = false) => (Name, Ascending) = (name, ascending);
}

[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnAttribute : Attribute
{
    public string? Name { get; }
    public VaultColumnAttribute(string? name = null) => Name = name;
}
