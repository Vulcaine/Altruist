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

/// <summary>
/// Declares a class as a vault model: a persisted table. Only properties marked with
/// <see cref="VaultColumnAttribute"/> become columns.
/// </summary>
/// <remarks>
/// At startup the SQL provider (Postgres) discovers every non-abstract <see cref="IVaultModel"/> class carrying
/// this attribute, registers it in <see cref="VaultRegistry"/>, plans its schema migration, and registers an
/// <see cref="IVault{TVaultModel}"/> singleton for it that you inject to query and save. Derive from
/// <see cref="VaultModel"/> to get the standard <c>id</c>/<c>version</c>/<c>created-at</c>/<c>type</c> columns,
/// primary key and optimistic concurrency.
/// </remarks>
/// <example>
/// <code>
/// [Vault("player_profile", StoreHistory: true)]
/// [VaultUniqueKey(nameof(Handle))]
/// public class PlayerProfileVault : VaultModel
/// {
///     [VaultColumn("handle")] public string Handle { get; set; } = "";
///     [VaultColumn, VaultColumnIndex] public int Level { get; set; }   // column "level"
///     [VaultColumn(nullable: true)] public string? Bio { get; set; }
/// }
/// // usage
/// var top = await profiles.Where(p =&gt; p.Level &gt;= 10).OrderByDescending(p =&gt; p.Level).Take(20).ToListAsync();
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class VaultAttribute : Attribute
{
    /// <summary>Table name. When blank, the snake_case class name is used.</summary>
    public string Name { get; }
    /// <summary>Schema (Postgres) or keyspace the table lives in; defaults to <c>altruist</c>.</summary>
    public string Keyspace { get; } = "altruist";
    /// <summary>Database provider token; defaults to <c>Postgres</c>. Currently informational: provider selection is driven by <c>altruist:persistence:database:provider</c>.</summary>
    public string DbToken { get; } = "Postgres";
    /// <summary>
    /// When true, a <c>&lt;table&gt;_history</c> table is maintained: saves called with <c>saveHistory: true</c> append a
    /// timestamped copy of the row, readable via <see cref="IVault{TVaultModel}.History"/>.
    /// </summary>
    public bool StoreHistory { get; }

    /// <summary>
    /// Target a specific named database instance. Empty means use the default instance.
    /// Matches the "name" field in altruist:persistence:database:instances config.
    /// </summary>
    public string DbInstance { get; } = "";

    /// <summary>Declares the vault model.</summary>
    /// <param name="Name">Table name (blank: snake_case of the class name).</param>
    /// <param name="StoreHistory">Whether to keep a history table (see <see cref="StoreHistory"/>).</param>
    /// <param name="Keyspace">Schema / keyspace name.</param>
    /// <param name="DbToken">Provider token (informational).</param>
    /// <param name="DbInstance">Named database instance from <c>altruist:persistence:database:instances</c>; empty for the default.</param>
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
    /// <summary>Human-readable reason, kept for documentation and migration logs.</summary>
    public string Reason { get; }
    /// <summary>Marks the table for deletion.</summary>
    /// <param name="reason">Why the table was removed (optional).</param>
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
    /// <summary>Name of the table the rows are copied into before the original is dropped.</summary>
    public string ArchiveTableName { get; }
    /// <summary>Human-readable reason, kept for documentation and migration logs.</summary>
    public string Reason { get; }
    /// <summary>Marks the table for archiving.</summary>
    /// <param name="archiveTableName">Target archive table name.</param>
    /// <param name="reason">Why the table was archived (optional).</param>
    public VaultArchivedAttribute(string archiveTableName, string reason = "")
    {
        ArchiveTableName = archiveTableName ?? throw new ArgumentNullException(nameof(archiveTableName));
        Reason = reason ?? "";
    }
}

/// <summary>
/// Declares the primary key of a vault model as one or more property names (composite when several).
/// <see cref="VaultModel"/> already declares <c>[VaultPrimaryKey(nameof(StorageId))]</c>; apply it only to override that.
/// </summary>
/// <remarks>
/// Saves upsert on the primary key unless a <see cref="VaultUniqueKeyAttribute"/> is present, in which case the
/// first unique key is used as the conflict target.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public class VaultPrimaryKeyAttribute : Attribute
{
    /// <summary>Property names (use <c>nameof</c>) forming the key, in order.</summary>
    public string[] Keys { get; }
    /// <summary>Declares the primary key.</summary>
    /// <param name="keys">Property names forming the key.</param>
    public VaultPrimaryKeyAttribute(params string[] keys) => Keys = keys;
}

/// <summary>
/// Declares a UNIQUE constraint over one or more columns. Can be applied several times.
/// </summary>
/// <remarks>
/// Important for saves: when present, <see cref="IVault{TVaultModel}.SaveAsync"/> upserts on the FIRST unique key
/// instead of the primary key, so saving an entity whose unique values match an existing row updates that row
/// and syncs its StorageId back onto the entity. Single-column unique keys also act as the column's index.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class VaultUniqueKeyAttribute : Attribute
{
    /// <summary>Property names (or physical column names) forming the constraint.</summary>
    public string[] Keys { get; }
    /// <summary>Declares the unique key.</summary>
    /// <param name="keys">Property names (preferably via <c>nameof</c>) or column names.</param>
    public VaultUniqueKeyAttribute(params string[] keys) => Keys = keys;
}

/// <summary>Creates a single-column index on this <see cref="VaultColumnAttribute"/> column during migration (skipped when the column is already covered by the primary key or a single-column unique key).</summary>
[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnIndexAttribute : Attribute { }

/// <summary>Excludes a property from persistence even if it carries <see cref="VaultColumnAttribute"/>. Properties without <see cref="VaultColumnAttribute"/> are already ignored.</summary>
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
    /// <summary>The previous physical column name.</summary>
    public string OldColumnName { get; }
    /// <summary>Records a previous column name.</summary>
    /// <param name="oldColumnName">The previous physical column name.</param>
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
    /// <summary>Source property name (resolved to its column) or physical column name to copy from.</summary>
    public string SourceColumn { get; }
    /// <summary>Requests a copy from <paramref name="sourceColumn"/> during migration.</summary>
    /// <param name="sourceColumn">Source property name or physical column name.</param>
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
    /// <summary>Human-readable reason, kept for documentation and migration logs.</summary>
    public string Reason { get; }
    /// <summary>Marks the column for deletion.</summary>
    /// <param name="reason">Why the column was removed (optional).</param>
    public VaultColumnDeleteAttribute(string reason = "")
        => Reason = reason ?? "";
}

/// <summary>
/// Names the property used as the table's sorting column. SQL migrations create an index on it (unless it is
/// already a primary or single-column unique key). It does not order query results; use
/// <see cref="IVault{TVaultModel}.OrderBy{TKey}"/> for that.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class VaultSortingByAttribute : Attribute
{
    /// <summary>Property name (or column name) of the sorting column.</summary>
    public string Name { get; }
    /// <summary>Intended sort direction; not consulted by the SQL migration planner.</summary>
    public bool Ascending { get; }
    /// <summary>Declares the sorting column.</summary>
    /// <param name="name">Property name of the sorting column.</param>
    /// <param name="ascending">Intended sort direction (default descending).</param>
    public VaultSortingByAttribute(
        string name,
        bool ascending = false) => (Name, Ascending) = (name, ascending);
}

/// <summary>
/// Maps a public instance property to a table column. Only properties with this attribute are persisted.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class VaultColumnAttribute : Attribute
{
    /// <summary>Physical column name; when null or blank the snake_case property name is used.</summary>
    public string? Name { get; }
    /// <summary>Whether the column is created as NULL-able (default NOT NULL).</summary>
    public bool Nullable { get; }
    /// <summary>Maps the property to a column.</summary>
    /// <param name="name">Physical column name (null: snake_case of the property name).</param>
    /// <param name="nullable">Allow NULL values in the column.</param>
    public VaultColumnAttribute(string? name = null, bool nullable = false)
    {
        Name = name;
        Nullable = nullable;
    }
}

/// <summary>
/// Declares a foreign key from this column to a column of another vault model; the migration creates the
/// constraint. Applies only to properties that also carry <see cref="VaultColumnAttribute"/>.
/// </summary>
/// <example>
/// <code>
/// [VaultColumn("owner_id"), VaultForeignKey(typeof(AccountVault), nameof(AccountVault.StorageId), VaultForeignKeyDeleteBehavior.SetNull)]
/// public string? OwnerId { get; set; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class VaultForeignKeyAttribute : Attribute
{
    /// <summary>The referenced vault model type.</summary>
    public Type PrincipalType { get; }
    /// <summary>Property name on <see cref="PrincipalType"/> of the referenced column.</summary>
    public string PrincipalPropertyName { get; }

    /// <summary>
    /// ON DELETE behavior: e.g. "CASCADE", "NO ACTION", "SET NULL", "SET DEFAULT", "RESTRICT".
    /// Defaults to "CASCADE".
    /// </summary>
    public string OnDelete { get; }

    /// <summary>Declares the foreign key.</summary>
    /// <param name="principalType">The referenced vault model type.</param>
    /// <param name="principalPropertyName">The referenced property (use <c>nameof</c>).</param>
    /// <param name="onDelete">ON DELETE behaviour, one of the <see cref="VaultForeignKeyDeleteBehavior"/> constants; blank means cascade.</param>
    public VaultForeignKeyAttribute(Type principalType, string principalPropertyName, string onDelete = VaultForeignKeyDeleteBehavior.Cascade)
    {
        PrincipalType = principalType ?? throw new ArgumentNullException(nameof(principalType));
        PrincipalPropertyName = principalPropertyName ?? throw new ArgumentNullException(nameof(principalPropertyName));
        OnDelete = string.IsNullOrWhiteSpace(onDelete) ? VaultForeignKeyDeleteBehavior.Cascade : onDelete;
    }
}


/// <summary>ON DELETE behaviours accepted by <see cref="VaultForeignKeyAttribute"/>.</summary>
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
