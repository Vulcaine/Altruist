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

/// <summary>
/// Marks an entire vault table for deletion during migration. The table is dropped from the DB.
/// The class stays in code as self-documenting history.
///
/// Apply [Obsolete] alongside this attribute to get compiler warnings/errors and IDE
/// strikethroughs wherever the vault is injected or referenced:
///
/// <example>
/// [VaultTableDelete("Replaced by PlayerStatsVault in v3.0")]
/// [Obsolete("This vault is deleted. Use PlayerStatsVault instead.", error: true)]
/// [Vault("old_player_stats")]
/// public class OldPlayerStatsVault : IStoredModel { ... }
/// </example>
///
/// - error: false → compiler WARNING (yellow squiggle, strikethrough)
/// - error: true  → compiler ERROR (red, won't compile if referenced)
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class VaultTableDeleteAttribute : Attribute
{
    public string Reason { get; }
    public VaultTableDeleteAttribute(string reason = "")
        => Reason = reason ?? "";
}

/// <summary>
/// Archives a vault table before dropping it. All data is copied to the archive table
/// using INSERT INTO ... SELECT, then the original table is dropped.
/// Safer than [VaultTableDelete] — data is preserved in the archive table.
///
/// <example>
/// [VaultArchived("archived_old_stats", "Migrated to PlayerStatsVault in v3.0")]
/// [Obsolete("Archived. Use PlayerStatsVault.", error: true)]
/// [Vault("old_player_stats")]
/// public class OldPlayerStatsVault : IStoredModel { ... }
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class VaultArchivedAttribute : Attribute
{
    public string ArchiveTableName { get; }
    public string Reason { get; }
    public VaultArchivedAttribute(string archiveTableName, string reason = "")
    {
        ArchiveTableName = archiveTableName ?? throw new ArgumentNullException(nameof(archiveTableName));
        Reason = reason ?? "";
    }
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

/// <summary>
/// Marks a property as renamed from a previous column name.
/// The migration planner will emit a RENAME COLUMN instead of DROP + ADD,
/// preserving existing data. Multiple attributes can be stacked to preserve
/// rename history — only the last one matching a current DB column is applied.
///
/// <example>
/// [VaultRenamedFrom("original_name")]   // first rename (historical)
/// [VaultRenamedFrom("display_name")]    // second rename — this one runs if "display_name" exists in DB
/// public string PrettyName { get; set; }
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class VaultRenamedFromAttribute : Attribute
{
    public string OldColumnName { get; }
    public VaultRenamedFromAttribute(string oldColumnName)
        => OldColumnName = oldColumnName ?? throw new ArgumentNullException(nameof(oldColumnName));
}

/// <summary>
/// Copies data from an existing column into this new column during migration,
/// with automatic type conversion (USING cast). The source column is NOT deleted —
/// use [VaultColumnDelete] on a separate property to remove it after copy.
///
/// Use nameof() for compile-time safety when the source property still exists,
/// or a string literal if it was already removed from the model.
///
/// <example>
/// // Copy int gold into new string gold_display with cast
/// [VaultColumn("gold_display")]
/// [VaultColumnCopy(nameof(Gold))]     // or [VaultColumnCopy("gold")] if Gold property removed
/// public string GoldDisplay { get; set; } = "";
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnCopyAttribute : Attribute
{
    public string SourceColumn { get; }
    public VaultColumnCopyAttribute(string sourceColumn)
        => SourceColumn = sourceColumn ?? throw new ArgumentNullException(nameof(sourceColumn));
}

/// <summary>
/// Marks a column for deletion during migration. The column is dropped from the DB.
/// The property serves as self-documenting history — it is ignored by the ORM
/// (implicitly treated as [VaultIgnore]) but read by the migration planner.
///
/// If [VaultColumnCopy] exists on another property referencing this column,
/// the copy runs first, then the delete.
///
/// Pair with [Obsolete] to get IDE strikethroughs and compiler warnings/errors:
///
/// <example>
/// [VaultColumnDelete("Replaced by GoldDisplay (string) in v2.3")]
/// [Obsolete("Column deleted. Use GoldDisplay instead.", error: true)]
/// [VaultColumn("gold")]
/// public int Gold { get; set; }
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnDeleteAttribute : Attribute
{
    public string Reason { get; }
    public VaultColumnDeleteAttribute(string reason = "")
        => Reason = reason ?? "";
}

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
