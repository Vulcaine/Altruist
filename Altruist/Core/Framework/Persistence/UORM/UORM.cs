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

namespace Altruist.UORM;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class VaultAttribute : Attribute
{
    public string Name { get; }
    public string Keyspace { get; } = "altruist";
    public string DbToken { get; } = "Postgres";
    public bool StoreHistory { get; }

    /// <summary>
    /// Target a specific named database instance. Empty means use the default instance.
    /// Matches the "name" field in altruist:persistence:database:instances config.
    /// </summary>
    public string DbInstance { get; } = "";

    public VaultAttribute(string Name, bool StoreHistory = false, string Keyspace = "altruist", string DbToken = "Postgres", string DbInstance = "")
        => (this.Name, this.StoreHistory, this.Keyspace, this.DbToken, this.DbInstance) = (Name, StoreHistory, Keyspace, DbToken, DbInstance);
}

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public class VaultPrimaryKeyAttribute : Attribute
{
    public string[] Keys { get; }
    public VaultPrimaryKeyAttribute(params string[] keys) => Keys = keys;
}

[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class VaultUniqueKeyAttribute : Attribute
{
    public string[] Keys { get; }
    public VaultUniqueKeyAttribute(params string[] keys) => Keys = keys;
}

[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnIndexAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property)]
public class VaultIgnoreAttribute : Attribute { }

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
    public bool Nullable { get; }
    public VaultColumnAttribute(string? name = null, bool nullable = false)
    {
        Name = name;
        Nullable = nullable;
    }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class VaultForeignKeyAttribute : Attribute
{
    public Type PrincipalType { get; }
    public string PrincipalPropertyName { get; }

    /// <summary>
    /// ON DELETE behavior: e.g. "CASCADE", "NO ACTION", "SET NULL", "SET DEFAULT", "RESTRICT".
    /// Defaults to "CASCADE".
    /// </summary>
    public string OnDelete { get; }

    public VaultForeignKeyAttribute(Type principalType, string principalPropertyName, string onDelete = VaultForeignKeyDeleteBehavior.Cascade)
    {
        PrincipalType = principalType ?? throw new ArgumentNullException(nameof(principalType));
        PrincipalPropertyName = principalPropertyName ?? throw new ArgumentNullException(nameof(principalPropertyName));
        OnDelete = string.IsNullOrWhiteSpace(onDelete) ? VaultForeignKeyDeleteBehavior.Cascade : onDelete;
    }
}


public static class VaultForeignKeyDeleteBehavior
{
    /// <summary>
    /// Delete child rows when the parent is deleted.
    /// Generates: ON DELETE CASCADE
    /// </summary>
    public const string Cascade = "CASCADE";

    /// <summary>
    /// Prevent deleting the parent if child rows exist (Postgres default).
    /// Generates: ON DELETE NO ACTION
    /// </summary>
    public const string NoAction = "NO ACTION";

    /// <summary>
    /// Prevent deleting the parent if child rows exist (checked immediately).
    /// Generates: ON DELETE RESTRICT
    /// </summary>
    public const string Restrict = "RESTRICT";

    /// <summary>
    /// Set the FK column to NULL when the parent is deleted.
    /// Generates: ON DELETE SET NULL
    /// </summary>
    public const string SetNull = "SET NULL";

    /// <summary>
    /// Set the FK column to its DEFAULT when the parent is deleted.
    /// Generates: ON DELETE SET DEFAULT
    /// </summary>
    public const string SetDefault = "SET DEFAULT";
}
