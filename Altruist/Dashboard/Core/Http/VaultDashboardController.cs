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

using System.Text.Json;

using Altruist.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Altruist.Dashboard;

/// <summary>
/// Dashboard API for database vaults (route <c>/dashboard/v1/vaults</c>): lists registered vault models with their
/// columns, pages through rows, edits rows by primary key, and runs raw SQL against SQL-backed (PostgreSQL) vaults.
/// </summary>
/// <remarks>
/// Only mapped when <c>altruist:dashboard:enabled</c> is <c>true</c>; every request must pass the dashboard protection
/// (<see cref="DashboardAccessOptions"/>). The query endpoint executes any SQL text it receives (including DDL and
/// DELETE) on the app's <c>NpgsqlDataSource</c>, so dashboard access amounts to database admin access. Row reads and edits go through the registered <see cref="IVault{T}"/>; an unknown
/// <c>typeKey</c> surfaces as the exception thrown by <see cref="VaultRegistry"/> (500).
/// </remarks>
[ApiController]
[Route("/dashboard/v1/vaults")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class VaultDashboardController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>Creates the controller.</summary>
    /// <param name="serviceProvider">Resolves <see cref="IVault{T}"/> instances and the PostgreSQL data source.</param>
    public VaultDashboardController(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    // ---------------- DTOs ----------------

    /// <summary>Body of <c>POST /dashboard/v1/vaults/{typeKey}/batch-update</c>.</summary>
    public sealed class VaultBatchUpdateRequestDto
    {
        /// <summary>Vault type key (the route value is what is used).</summary>
        public string TypeKey { get; set; } = default!;

        // Each item: { fieldName -> newValue }
        // MUST include all primary key fields
        /// <summary>Rows as field name to new value; each must include every primary-key field.</summary>
        public List<Dictionary<string, object?>> Items { get; set; } = new();
    }

    /// <summary>Response of the batch-update endpoint.</summary>
    public sealed class VaultBatchUpdateResultDto
    {
        /// <summary>Number of rows passed to <c>UpdateAsync</c>.</summary>
        public int Updated { get; set; }
    }

    /// <summary>One column of a vault model.</summary>
    public sealed class VaultColumnDto
    {
        /// <summary>Model property name.</summary>
        public string FieldName { get; set; } = default!;
        /// <summary>Physical column name.</summary>
        public string ColumnName { get; set; } = default!;
        /// <summary>CLR type name of the field.</summary>
        public string ClrType { get; set; } = default!;
        /// <summary>Whether the column is nullable.</summary>
        public bool IsNullable { get; set; }
        /// <summary>Part of the primary key.</summary>
        public bool IsPrimaryKey { get; set; }
        /// <summary>Has an index.</summary>
        public bool IsIndexed { get; set; }
        /// <summary>Part of a unique constraint.</summary>
        public bool IsUnique { get; set; }
        /// <summary>Is a foreign key.</summary>
        public bool IsForeignKey { get; set; }
    }

    /// <summary>One registered vault model, as listed by <c>GET /dashboard/v1/vaults</c>.</summary>
    public sealed class VaultDefinitionDto
    {
        /// <summary>Registry key used in the routes.</summary>
        public string TypeKey { get; set; } = default!;
        /// <summary>Assembly-qualified model type name.</summary>
        public string ClrType { get; set; } = default!;
        /// <summary>Model type name without namespace or generic arity.</summary>
        public string ClrTypeShort { get; set; } = default!;
        /// <summary>Keyspace / schema.</summary>
        public string Keyspace { get; set; } = default!;
        /// <summary>Table (document) name.</summary>
        public string TableName { get; set; } = default!;
        /// <summary>Whether history rows are stored.</summary>
        public bool StoreHistory { get; set; }
        /// <summary>Whether the query endpoint can be used (SQL-backed vault).</summary>
        public bool SupportsSqlQuery { get; set; }
        /// <summary>Model columns.</summary>
        public IReadOnlyList<VaultColumnDto> Columns { get; set; } = Array.Empty<VaultColumnDto>();
    }

    /// <summary>Response of <c>GET /dashboard/v1/vaults/{typeKey}/items</c>.</summary>
    public sealed class VaultItemPageDto
    {
        /// <summary>Vault type key.</summary>
        public string TypeKey { get; set; } = default!;
        /// <summary>Effective skip.</summary>
        public int Skip { get; set; }
        /// <summary>Effective page size.</summary>
        public int Take { get; set; }
        /// <summary>Total row count.</summary>
        public long Total { get; set; }
        /// <summary>Field names, in model order.</summary>
        public IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();
        /// <summary>Rows as field name to value.</summary>
        public List<Dictionary<string, object?>> Items { get; set; } = new();
    }

    /// <summary>Body of <c>POST /dashboard/v1/vaults/{typeKey}/query</c>.</summary>
    public sealed class VaultQueryRequestDto
    {
        /// <summary>SQL to execute.</summary>
        public string Sql { get; set; } = string.Empty;
    }

    /// <summary>Response of <c>POST /dashboard/v1/vaults/{typeKey}/query</c>.</summary>
    public sealed class VaultQueryResultDto
    {
        /// <summary>Whether the statement was run as a query returning rows.</summary>
        public bool HasRowset { get; set; }
        /// <summary>Lower-case first keyword (<c>select</c>, <c>update</c>, ...), or <c>statement</c>.</summary>
        public string StatementKind { get; set; } = "unknown";
        /// <summary>Rows affected (non-rowset statements only).</summary>
        public long? AffectedRows { get; set; }
        /// <summary>Result column names.</summary>
        public IReadOnlyList<string> Columns { get; set; } = Array.Empty<string>();
        /// <summary>Result rows as column name to value.</summary>
        public List<Dictionary<string, object?>> Rows { get; set; } = new();
    }

    // ---------------- Helpers ----------------

    private static string GetShortTypeName(Type t)
    {
        var name = t.Name;
        var idx = name.IndexOf('`');
        return idx > 0 ? name[..idx] : name;
    }

    private VaultDefinitionDto BuildDefinition(VaultMetadata md)
    {
        var doc = VaultDocument.From(md.ClrType);

        var pkFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.PrimaryKey?.Keys is { Length: > 0 })
        {
            foreach (var k in doc.PrimaryKey.Keys)
                pkFieldNames.Add(k);
        }

        var indexedPhysical = new HashSet<string>(
            doc.Indexes ?? [],
            StringComparer.OrdinalIgnoreCase);

        var uniquePhysical = new HashSet<string>(
            doc.UniqueKeys.SelectMany(uk => uk.Columns),
            StringComparer.OrdinalIgnoreCase);

        var fkByProperty = new HashSet<string>(
            doc.ForeignKeys.Select(fk => fk.PropertyName),
            StringComparer.OrdinalIgnoreCase);

        var columns = new List<VaultColumnDto>();

        foreach (var field in doc.Fields)
        {
            var physical = doc.Columns.TryGetValue(field, out var c) ? c : field;

            doc.FieldTypes.TryGetValue(field, out var clrType);

            columns.Add(new VaultColumnDto
            {
                FieldName = field,
                ColumnName = physical,
                ClrType = clrType?.Name ?? "object",
                IsNullable = doc.NullableColumns.Contains(physical),
                IsPrimaryKey =
                    pkFieldNames.Contains(field) ||
                    (doc.PrimaryKey?.Keys?.Any(k =>
                        string.Equals(k, physical, StringComparison.OrdinalIgnoreCase)) ?? false),
                IsIndexed = indexedPhysical.Contains(physical),
                IsUnique = uniquePhysical.Contains(physical),
                IsForeignKey = fkByProperty.Contains(field)
            });
        }

        return new VaultDefinitionDto
        {
            TypeKey = md.TypeKey,
            ClrType = md.ClrType.AssemblyQualifiedName
                      ?? md.ClrType.FullName
                      ?? md.ClrType.Name,
            ClrTypeShort = GetShortTypeName(md.ClrType),
            Keyspace = md.Keyspace,
            TableName = doc.Name,
            StoreHistory = doc.StoreHistory,
            SupportsSqlQuery = SupportsSqlQuery(md),
            Columns = columns
        };
    }

    private static Dictionary<string, object?> BuildRow(VaultDocument doc, object entity)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in doc.Fields)
        {
            if (doc.PropertyAccessors.TryGetValue(field, out var accessor))
                row[field] = accessor(entity);
            else
                row[field] = null;
        }

        return row;
    }

    // ---------------- Endpoints ----------------

    /// <summary>
    /// <c>POST /dashboard/v1/vaults/{typeKey}/batch-update</c> with a <see cref="VaultBatchUpdateRequestDto"/>: for each
    /// item, finds the row by its primary-key fields, sets the remaining known fields (unknown fields are ignored; field
    /// names match case-insensitively; items with no changes are skipped) and saves it through the vault, so
    /// <c>OnSave</c> runs and versions bump. Every item is validated before any is applied, and all saves run in one
    /// database transaction: either every row is updated or none. SQL-backed (PostgreSQL) vaults only.
    /// 200 with the number of updated rows; 400 for a request that cannot be applied (missing primary-key field, a field
    /// given twice in different case, a value of the wrong type, a vault that is not SQL-backed or has no primary key);
    /// 404 when a primary key matches no row (nothing is changed).
    /// </summary>
    /// <param name="typeKey">Vault type key from <see cref="VaultDefinitionDto.TypeKey"/>.</param>
    /// <param name="request">Rows to update.</param>
    /// <param name="ct">Cancels the update; the transaction then rolls back.</param>
    [HttpPost("{typeKey}/batch-update")]
    public async Task<ActionResult<VaultBatchUpdateResultDto>> BatchUpdate(
    string typeKey,
    [FromBody] VaultBatchUpdateRequestDto request,
    CancellationToken ct = default)
    {
        if (request.Items is null || request.Items.Count == 0)
            return Ok(new VaultBatchUpdateResultDto { Updated = 0 });

        var md = VaultRegistry.GetByTypeKey(typeKey);
        if (!SupportsSqlQuery(md))
            return BadRequest($"Batch updates are supported only for SQL-backed vaults. '{typeKey}' is not SQL-backed.");

        var dataSource = _serviceProvider.GetService<NpgsqlDataSource>();
        if (dataSource is null)
            return BadRequest("PostgreSQL data source is not available.");

        var doc = VaultDocument.From(md.ClrType);
        if (doc.PrimaryKey is null)
            return BadRequest($"Vault '{typeKey}' has no primary key.");

        var plan = new List<RowUpdate>(request.Items.Count);
        foreach (var item in request.Items)
        {
            var (update, error) = PlanRowUpdate(doc, item);
            if (error is not null)
                return BadRequest(error);
            if (update!.Changes.Count > 0)
                plan.Add(update);
        }

        var vaultType = typeof(IVault<>).MakeGenericType(md.ClrType);
        dynamic vault = _serviceProvider.GetRequiredService(vaultType);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        using (SqlAmbientTransaction.Enter(conn, tx))
        {
            foreach (var update in plan)
            {
                ct.ThrowIfCancellationRequested();
                object? entity = await vault.Where((dynamic)PrimaryKeyPredicate(md.ClrType, update.PrimaryKey)).FirstOrDefaultAsync(ct);
                if (entity is null)
                    return NotFound($"No '{typeKey}' row with primary key {FormatKey(update.PrimaryKey)}; nothing was updated.");

                foreach (var (field, value) in update.Changes)
                    md.ClrType.GetProperty(field)!.SetValue(entity, value);

                await vault.SaveAsync((dynamic)entity, false, ct);
            }
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);

        return Ok(new VaultBatchUpdateResultDto { Updated = plan.Count });
    }

    private sealed record RowUpdate(IReadOnlyDictionary<string, object?> PrimaryKey, IReadOnlyDictionary<string, object?> Changes);

    // Field names are the model's property names, matched case-insensitively (doc.FieldTypes is case-insensitive);
    // the returned dictionaries use the property's own spelling.
    private static (RowUpdate? Update, string? Error) PlanRowUpdate(VaultDocument doc, Dictionary<string, object?> item)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in item)
        {
            if (!row.TryAdd(field, value))
                return (null, $"Field '{field}' is given more than once (field names are case-insensitive).");
        }

        var pkFields = doc.PrimaryKey!.Keys;
        var pk = new Dictionary<string, object?>(StringComparer.Ordinal);
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, raw) in row)
        {
            var name = doc.FieldTypes.Keys.FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase));
            if (name is null)
                continue;

            object? value;
            try
            {
                value = ConvertIncomingValue(raw, doc.FieldTypes[name]);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or InvalidOperationException
                                           or JsonException or ArgumentException or OverflowException)
            {
                return (null, $"Field '{field}': {ex.Message}");
            }

            if (pkFields.Contains(name, StringComparer.OrdinalIgnoreCase))
                pk[name] = value;
            else
                changes[name] = value;
        }

        var missing = pkFields.FirstOrDefault(k => !pk.Keys.Contains(k, StringComparer.OrdinalIgnoreCase));
        return missing is null
            ? (new RowUpdate(pk, changes), null)
            : (null, $"Missing primary key field '{missing}'.");
    }

    private static System.Linq.Expressions.LambdaExpression PrimaryKeyPredicate(Type modelType, IReadOnlyDictionary<string, object?> pk)
    {
        var x = System.Linq.Expressions.Expression.Parameter(modelType, "x");
        System.Linq.Expressions.Expression? body = null;
        foreach (var (field, value) in pk)
        {
            var property = System.Linq.Expressions.Expression.Property(x, field);
            var equal = System.Linq.Expressions.Expression.Equal(property,
                System.Linq.Expressions.Expression.Constant(value, property.Type));
            body = body is null ? equal : System.Linq.Expressions.Expression.AndAlso(body, equal);
        }
        return System.Linq.Expressions.Expression.Lambda(
            typeof(Func<,>).MakeGenericType(modelType, typeof(bool)), body!, x);
    }

    private static string FormatKey(IReadOnlyDictionary<string, object?> pk)
        => string.Join(", ", pk.Select(kv => $"{kv.Key}={kv.Value}"));

    private static object? ConvertIncomingValue(object? rawValue, Type targetType)
    {
        if (rawValue is null)
            return null;

        // If JSON came in as JsonElement, convert it properly
        if (rawValue is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Null)
                return null;

            if (targetType == typeof(string))
                return je.GetString();

            if (targetType == typeof(int))
                return je.GetInt32();

            if (targetType == typeof(long))
                return je.GetInt64();

            if (targetType == typeof(bool))
                return je.GetBoolean();

            if (targetType == typeof(float))
                return je.GetSingle();

            if (targetType == typeof(double))
                return je.GetDouble();

            if (targetType == typeof(DateTime))
                return je.GetDateTime();

            if (targetType.IsEnum)
                return Enum.Parse(targetType, je.GetString()!, ignoreCase: true);

            return JsonSerializer.Deserialize(je.GetRawText(), targetType);
        }

        if (targetType.IsAssignableFrom(rawValue.GetType()))
            return rawValue;

        return Convert.ChangeType(rawValue, targetType);
    }

    /// <summary>
    /// <c>GET /dashboard/v1/vaults</c>: 200 with every registered vault as <see cref="VaultDefinitionDto"/>, ordered by
    /// keyspace then type key.
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<VaultDefinitionDto>> GetVaults()
    {
        var defs = VaultRegistry.GetAll()
            .OrderBy(md => md.Keyspace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(md => md.TypeKey, StringComparer.Ordinal)
            .Select(BuildDefinition)
            .ToList();

        return Ok(defs);
    }

    /// <summary>
    /// <c>POST /dashboard/v1/vaults/{typeKey}/query</c> with a <see cref="VaultQueryRequestDto"/>: sets
    /// <c>search_path</c> to the vault's keyspace and executes the SQL as-is on a pooled PostgreSQL connection.
    /// SELECT/WITH/SHOW/VALUES/EXPLAIN return rows (JSON, byte arrays as base64, arrays as lists); any other statement
    /// returns <see cref="VaultQueryResultDto.AffectedRows"/>. 400 when the SQL is blank, the vault is not SQL-backed or
    /// no data source is registered.
    /// </summary>
    /// <param name="typeKey">Vault type key.</param>
    /// <param name="request">SQL text.</param>
    /// <param name="ct">Cancels the database calls.</param>
    [HttpPost("{typeKey}/query")]
    public async Task<ActionResult<VaultQueryResultDto>> QueryVault(
        string typeKey,
        [FromBody] VaultQueryRequestDto request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
            return BadRequest("SQL is required.");

        var md = VaultRegistry.GetByTypeKey(typeKey);
        if (!SupportsSqlQuery(md))
            return BadRequest($"Raw SQL is supported only for SQL-backed vaults. '{typeKey}' is not SQL-backed.");

        var dataSource = _serviceProvider.GetService<NpgsqlDataSource>();
        if (dataSource is null)
            return BadRequest("PostgreSQL data source is not available.");

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using (var searchPath = conn.CreateCommand())
        {
            searchPath.CommandText = $"SET search_path TO \"{EscapeIdentifier(md.Keyspace)}\";";
            await searchPath.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var sql = request.Sql.Trim();
        var result = new VaultQueryResultDto
        {
            HasRowset = LooksLikeRowsetQuery(sql),
            StatementKind = GetStatementKind(sql)
        };

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        if (!result.HasRowset)
        {
            result.AffectedRows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return Ok(result);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var columns = new List<string>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        result.Columns = columns;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                object? value = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false)
                    ? null
                    : NormalizeDbValue(reader.GetValue(i));
                row[columns[i]] = value;
            }

            result.Rows.Add(row);
        }

        return Ok(result);
    }

    /// <summary>
    /// <c>GET /dashboard/v1/vaults/{typeKey}/items?skip=0&amp;take=50</c>: 200 with a <see cref="VaultItemPageDto"/>: the
    /// total row count and one page of rows (field name to value) read through <see cref="IVault{T}"/>.
    /// </summary>
    /// <param name="typeKey">Vault type key.</param>
    /// <param name="skip">Rows to skip (negative becomes 0).</param>
    /// <param name="take">Page size (0 or less becomes 50; capped at 500).</param>
    /// <param name="ct">Not observed.</param>
    [HttpGet("{typeKey}/items")]
    public async Task<ActionResult<VaultItemPageDto>> GetVaultItems(
        string typeKey,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take <= 0)
            take = 50;
        if (take > 500)
            take = 500;
        if (skip < 0)
            skip = 0;

        var md = VaultRegistry.GetByTypeKey(typeKey);
        var doc = VaultDocument.From(md.ClrType);

        var (total, items) = await QueryVaultAsync(md.ClrType, skip, take);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var entity in items)
            rows.Add(BuildRow(doc, entity));

        return Ok(new VaultItemPageDto
        {
            TypeKey = md.TypeKey,
            Skip = skip,
            Take = take,
            Total = total,
            Fields = doc.Fields,
            Items = rows
        });
    }

    // ---------------- Core query logic (no reflection) ----------------

    private async Task<(long Total, IEnumerable<object> Items)>
        QueryVaultAsync(Type modelType, int skip, int take)
    {
        var vaultType = typeof(IVault<>).MakeGenericType(modelType);
        dynamic vault = _serviceProvider.GetRequiredService(vaultType);

        long total = await vault.CountAsync();

        if (skip > 0)
            vault = vault.Skip(skip);

        if (take > 0)
            vault = vault.Take(take);

        IEnumerable<object> list = await vault.ToListAsync();
        return (total, list);
    }

    private bool SupportsSqlQuery(VaultMetadata metadata)
    {
        var vaultType = typeof(IVault<>).MakeGenericType(metadata.ClrType);
        var vault = _serviceProvider.GetService(vaultType);
        return vault is not null && IsSqlBackedVault(vault.GetType());
    }

    private static bool IsSqlBackedVault(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType!)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(SqlVault<>))
                return true;
        }

        return false;
    }

    private static string EscapeIdentifier(string identifier)
        => identifier.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static string GetStatementKind(string sql)
    {
        var first = sql
            .TrimStart()
            .Split([' ', '\t', '\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim()
            .ToUpperInvariant();

        return first switch
        {
            "SELECT" => "select",
            "WITH" => "with",
            "SHOW" => "show",
            "VALUES" => "values",
            "EXPLAIN" => "explain",
            "UPDATE" => "update",
            "INSERT" => "insert",
            "DELETE" => "delete",
            _ => "statement"
        };
    }

    private static bool LooksLikeRowsetQuery(string sql)
    {
        var kind = GetStatementKind(sql);
        return kind is "select" or "with" or "show" or "values" or "explain";
    }

    private static object? NormalizeDbValue(object? value)
    {
        if (value is null or DBNull)
            return null;

        if (value is JsonDocument document)
            return JsonSerializer.Deserialize<object?>(document.RootElement.GetRawText());

        if (value is byte[] bytes)
            return Convert.ToBase64String(bytes);

        if (value is Array array && value is not byte[])
        {
            var list = new List<object?>(array.Length);
            foreach (var item in array)
                list.Add(NormalizeDbValue(item));
            return list;
        }

        if (value is string str)
        {
            var trimmed = str.Trim();
            if ((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
                || (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)))
            {
                try
                {
                    return JsonSerializer.Deserialize<object?>(trimmed);
                }
                catch
                {
                    return str;
                }
            }

            return str;
        }

        return value;
    }
}
