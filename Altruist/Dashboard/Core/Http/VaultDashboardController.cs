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

    // ---------------- Helpers ----------------

    private static string GetShortTypeName(Type t)
    {
        var name = t.Name;
        var idx = name.IndexOf('`');
        return idx > 0 ? name[..idx] : name;
    }

    private static VaultDefinitionDto BuildDefinition(VaultMetadata md)
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
}
