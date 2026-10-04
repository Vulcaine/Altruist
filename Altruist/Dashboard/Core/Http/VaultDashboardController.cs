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

[ApiController]
[Route("/dashboard/v1/vaults")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class VaultDashboardController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;

    public VaultDashboardController(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    // ---------------- DTOs ----------------

    public sealed class VaultBatchUpdateRequestDto
    {
        public string TypeKey { get; set; } = default!;

        // Each item: { fieldName -> newValue }
        // MUST include all primary key fields
        public List<Dictionary<string, object?>> Items { get; set; } = new();
    }

    public sealed class VaultBatchUpdateResultDto
    {
        public int Updated { get; set; }
    }

    public sealed class VaultColumnDto
    {
        public string FieldName { get; set; } = default!;
        public string ColumnName { get; set; } = default!;
        public string ClrType { get; set; } = default!;
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsIndexed { get; set; }
        public bool IsUnique { get; set; }
        public bool IsForeignKey { get; set; }
    }

    public sealed class VaultDefinitionDto
    {
        public string TypeKey { get; set; } = default!;
        public string ClrType { get; set; } = default!;
        public string ClrTypeShort { get; set; } = default!;
        public string Keyspace { get; set; } = default!;
        public string TableName { get; set; } = default!;
        public bool StoreHistory { get; set; }
        public bool SupportsSqlQuery { get; set; }
        public IReadOnlyList<VaultColumnDto> Columns { get; set; } = Array.Empty<VaultColumnDto>();
    }

    public sealed class VaultItemPageDto
    {
        public string TypeKey { get; set; } = default!;
        public int Skip { get; set; }
        public int Take { get; set; }
        public long Total { get; set; }
        public IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();
        public List<Dictionary<string, object?>> Items { get; set; } = new();
    }

    public sealed class VaultQueryRequestDto
    {
        public string Sql { get; set; } = string.Empty;
    }

    public sealed class VaultQueryResultDto
    {
        public bool HasRowset { get; set; }
        public string StatementKind { get; set; } = "unknown";
        public long? AffectedRows { get; set; }
        public IReadOnlyList<string> Columns { get; set; } = Array.Empty<string>();
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

    [HttpPost("{typeKey}/batch-update")]
    public async Task<ActionResult<VaultBatchUpdateResultDto>> BatchUpdate(
    string typeKey,
    [FromBody] VaultBatchUpdateRequestDto request,
    CancellationToken ct = default)
    {
        if (request.Items is null || request.Items.Count == 0)
            return Ok(new VaultBatchUpdateResultDto { Updated = 0 });

        var md = VaultRegistry.GetByTypeKey(typeKey);
        var doc = VaultDocument.From(md.ClrType);

        var pkFields = doc.PrimaryKey?.Keys
            ?? throw new InvalidOperationException("Vault has no primary key.");

        // Resolve IVault<T>
        var vaultType = typeof(IVault<>).MakeGenericType(md.ClrType);
        dynamic vault = _serviceProvider.GetRequiredService(vaultType);

        int updated = 0;

        foreach (var row in request.Items)
        {
            // --- Extract primary key values ---
            var pk = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pkField in pkFields)
            {
                if (!row.TryGetValue(pkField, out var rawPkValue))
                    throw new InvalidOperationException(
                        $"Missing primary key field '{pkField}'.");

                pk[pkField] = ConvertIncomingValue(rawPkValue, doc.FieldTypes[pkField]);
            }

            // --- Extract changed fields ---
            var changes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (field, rawValue) in row)
            {
                if (pkFields.Contains(field))
                    continue;

                if (!doc.FieldTypes.TryGetValue(field, out var targetType))
                    continue;

                var converted = ConvertIncomingValue(rawValue, targetType);
                changes[field] = converted;
            }

            if (changes.Count == 0)
                continue;

            await vault.UpdateAsync(pk, changes);
            updated++;
        }

        return Ok(new VaultBatchUpdateResultDto { Updated = updated });
    }

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
