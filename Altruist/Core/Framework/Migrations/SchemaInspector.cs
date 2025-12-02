
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

using Altruist.Persistence;

namespace Altruist.Migrations;

public interface ISchemaInspector
{
    Task<DatabaseModel> GetCurrentModelAsync(string schema, CancellationToken ct = default);
}

public abstract class AbstractSchemaInspector : ISchemaInspector
{
    protected readonly ISqlDatabaseProvider _provider;

    protected AbstractSchemaInspector(ISqlDatabaseProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    protected readonly struct SchemaSnapshot
    {
        public readonly Dictionary<string, Dictionary<string, ColumnModel>> ColumnsByTable;
        public readonly Dictionary<string, List<string>> PrimaryKeysByTable;
        public readonly Dictionary<string, Dictionary<string, string>> UniqueConstraintsByTable;
        public readonly Dictionary<string, Dictionary<string, IndexModel>> IndexesByTable;
        public readonly Dictionary<string, List<ForeignKeyModel>> ForeignKeysByTable;

        public SchemaSnapshot(
            Dictionary<string, Dictionary<string, ColumnModel>> columnsByTable,
            Dictionary<string, List<string>> primaryKeysByTable,
            Dictionary<string, Dictionary<string, string>> uniqueConstraintsByTable,
            Dictionary<string, Dictionary<string, IndexModel>> indexesByTable,
            Dictionary<string, List<ForeignKeyModel>> foreignKeysByTable)
        {
            ColumnsByTable = columnsByTable ?? throw new ArgumentNullException(nameof(columnsByTable));
            PrimaryKeysByTable = primaryKeysByTable ?? throw new ArgumentNullException(nameof(primaryKeysByTable));
            UniqueConstraintsByTable = uniqueConstraintsByTable ?? throw new ArgumentNullException(nameof(uniqueConstraintsByTable));
            IndexesByTable = indexesByTable ?? throw new ArgumentNullException(nameof(indexesByTable));
            ForeignKeysByTable = foreignKeysByTable ?? throw new ArgumentNullException(nameof(foreignKeysByTable));
        }
    }

    public async Task<DatabaseModel> GetCurrentModelAsync(string schema, CancellationToken ct = default)
    {
        if (schema is null)
            throw new ArgumentNullException(nameof(schema));

        var schemaName = NormalizeSchemaName(schema);

        var snapshot = await LoadSchemaAsync(schemaName, ct).ConfigureAwait(false);

        var tables = new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tableName, columnDict) in snapshot.ColumnsByTable)
        {
            snapshot.PrimaryKeysByTable.TryGetValue(tableName, out var pkCols);
            snapshot.UniqueConstraintsByTable.TryGetValue(tableName, out var uqByColumn);
            snapshot.IndexesByTable.TryGetValue(tableName, out var ixDict);
            snapshot.ForeignKeysByTable.TryGetValue(tableName, out var fkList);

            var tableModel = new TableModel(
                name: tableName,
                columns: columnDict,
                primaryKeyColumns: pkCols ?? [],
                uniqueConstraints: uqByColumn ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                indexes: ixDict ?? new Dictionary<string, IndexModel>(StringComparer.OrdinalIgnoreCase),
                foreignKeys: fkList ?? new List<ForeignKeyModel>());

            tables[tableName] = tableModel;
        }

        return new DatabaseModel(schemaName, tables);
    }

    /// <summary>
    /// Provider-specific schema inspection. Should query the database and return
    /// a snapshot of columns, primary keys, uniques, indexes, and foreign keys
    /// for the given schema.
    /// </summary>
    protected abstract Task<SchemaSnapshot> LoadSchemaAsync(string schemaName, CancellationToken ct);

    /// <summary>
    /// Default schema name for this provider (e.g. "public", "dbo").
    /// </summary>
    protected virtual string GetDefaultSchemaName() => "public";

    /// <summary>
    /// Normalizes schema names for comparison and model building.
    /// </summary>
    protected virtual string NormalizeSchemaName(string? schemaName)
    {
        var s = schemaName;
        if (string.IsNullOrWhiteSpace(s))
            s = GetDefaultSchemaName();

        return s.Trim().ToLowerInvariant();
    }
}
