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

using System.Collections;
using System.Text.Json;

using Altruist.Persistence;

using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard;

[ApiController]
[Route("/dashboard/v1/vaults")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class VaultDashboardController : ControllerBase
{
    private readonly IRepositoryFactory _repositoryFactory;
    private readonly JsonSerializerOptions _jsonOptions;

    public VaultDashboardController(
        IRepositoryFactory repositoryFactory,
        JsonSerializerOptions jsonOptions)
    {
        _repositoryFactory = repositoryFactory;
        _jsonOptions = jsonOptions;
    }

    // ---------- DTOs ----------

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

        // Logical field names, in display order
        public IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();

        // One dictionary per row: fieldName -> value
        public List<Dictionary<string, object?>> Items { get; set; } = new();
    }

    // ---------- Helpers ----------

    private static string GetShortTypeName(Type t)
    {
        var name = t.Name;
        var idx = name.IndexOf('`');
        return idx > 0 ? name[..idx] : name;
    }

    private static VaultDefinitionDto BuildDefinition(VaultMetadata md)
    {
        var doc = Document.From(md.ClrType);

        // Precompute PK / index / unique / FK sets
        var pkFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (doc.PrimaryKey?.Keys is { Length: > 0 })
        {
            foreach (var k in doc.PrimaryKey.Keys)
            {
                if (!string.IsNullOrWhiteSpace(k))
                    pkFieldNames.Add(k);
            }
        }

        var indexedPhysical = new HashSet<string>(doc.Indexes ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var uniquePhysical = new HashSet<string>(
            doc.UniqueKeys.SelectMany(uk => uk.Columns),
            StringComparer.OrdinalIgnoreCase);

        var fkByProperty = new HashSet<string>(
            doc.ForeignKeys.Select(fk => fk.PropertyName),
            StringComparer.OrdinalIgnoreCase);

        var columns = new List<VaultColumnDto>();

        foreach (var fieldName in doc.Fields)
        {
            var physical = doc.Columns.TryGetValue(fieldName, out var col)
                ? col
                : fieldName;

            var isNullable = doc.NullableColumns.Contains(physical);

            doc.FieldTypes.TryGetValue(fieldName, out var clrType);
            var clrName = clrType?.Name ?? "object";

            // PK is defined by property name OR physical column in [VaultPrimaryKey]
            var isPk = pkFieldNames.Contains(fieldName) ||
                       (doc.PrimaryKey?.Keys?.Any(k =>
                           string.Equals(k, physical, StringComparison.OrdinalIgnoreCase)) ?? false);

            var isIndexed = indexedPhysical.Contains(physical);
            var isUnique = uniquePhysical.Contains(physical);
            var isFk = fkByProperty.Contains(fieldName);

            columns.Add(new VaultColumnDto
            {
                FieldName = fieldName,
                ColumnName = physical,
                ClrType = clrName,
                IsNullable = isNullable,
                IsPrimaryKey = isPk,
                IsIndexed = isIndexed,
                IsUnique = isUnique,
                IsForeignKey = isFk
            });
        }

        return new VaultDefinitionDto
        {
            TypeKey = md.TypeKey,
            ClrType = md.ClrType.AssemblyQualifiedName ?? md.ClrType.FullName ?? md.ClrType.Name,
            ClrTypeShort = GetShortTypeName(md.ClrType),
            Keyspace = md.Keyspace,
            TableName = doc.Name,
            StoreHistory = doc.StoreHistory,
            Columns = columns
        };
    }

    private static Dictionary<string, object?> BuildRow(Document doc, object entity)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in doc.Fields)
        {
            if (doc.PropertyAccessors.TryGetValue(field, out var accessor))
            {
                var value = accessor(entity);

                // Normalize to something JSON can handle.
                row[field] = value;
            }
            else
            {
                row[field] = null;
            }
        }

        return row;
    }

    // ---------- Endpoints ----------

    /// <summary>
    /// List all registered vault types and their structure.
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<VaultDefinitionDto>> GetVaults()
    {
        var metadata = VaultRegistry.GetAll();
        var defs = metadata
            .OrderBy(md => md.Keyspace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(md => md.TypeKey, StringComparer.Ordinal)
            .Select(BuildDefinition)
            .ToList();

        return Ok(defs);
    }

    /// <summary>
    /// Get a page of items for a given vault type.
    /// Query parameters:
    ///   skip: how many items to skip (default 0)
    ///   take: page size (default 50, max 500)
    /// </summary>
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
        var modelType = md.ClrType;
        var doc = Document.From(modelType);

        // Get repository for this keyspace
        var repo = _repositoryFactory.Make(md.Keyspace);

        // repo.Select<TModel>()
        var selectMethod = typeof(IAnyVaultRepository)
            .GetMethod(nameof(IAnyVaultRepository.Select))!
            .MakeGenericMethod(modelType);

        var vaultObj = selectMethod.Invoke(repo, null)
                      ?? throw new InvalidOperationException("Vault Select returned null.");

        var vaultInterfaceType = typeof(IVault<>).MakeGenericType(modelType);

        var skipMethod = vaultInterfaceType.GetMethod(nameof(IVault<IVaultModel>.Skip))!;
        var takeMethod = vaultInterfaceType.GetMethod(nameof(IVault<IVaultModel>.Take))!;
        var countAsyncMethod = vaultInterfaceType.GetMethod(nameof(IVault<IVaultModel>.CountAsync))!;
        var toListAsyncMethod = vaultInterfaceType.GetMethod(nameof(IVault<IVaultModel>.ToListAsync), Type.EmptyTypes)!;

        // Count on the full vault
        var totalTaskObj = countAsyncMethod.Invoke(vaultObj, Array.Empty<object>())
                           ?? throw new InvalidOperationException("CountAsync returned null Task.");
        var totalTask = (Task)totalTaskObj;
        await totalTask.ConfigureAwait(false);
        var totalResultProp = totalTask.GetType().GetProperty("Result")!;
        var total = (long)(totalResultProp.GetValue(totalTask) ?? 0L);

        // Apply pagination
        object pagedVault = vaultObj;
        if (skip > 0)
        {
            pagedVault = skipMethod.Invoke(pagedVault, new object[] { skip }) ?? pagedVault;
        }

        if (take > 0)
        {
            pagedVault = takeMethod.Invoke(pagedVault, new object[] { take }) ?? pagedVault;
        }

        // ToListAsync on paged vault
        var listTaskObj = toListAsyncMethod.Invoke(pagedVault, Array.Empty<object>())
                          ?? throw new InvalidOperationException("ToListAsync returned null Task.");

        var listTask = (Task)listTaskObj;
        await listTask.ConfigureAwait(false);
        var listResultProp = listTask.GetType().GetProperty("Result")!;
        var listObj = listResultProp.GetValue(listTask)
                      ?? throw new InvalidOperationException("ToListAsync Result is null.");

        var listEnumerable = (IEnumerable)listObj;

        // Materialize rows as fieldName -> value
        var rows = new List<Dictionary<string, object?>>();

        foreach (var entity in listEnumerable)
        {
            rows.Add(BuildRow(doc, entity!));
        }

        var dto = new VaultItemPageDto
        {
            TypeKey = md.TypeKey,
            Skip = skip,
            Take = take,
            Total = total,
            Fields = doc.Fields,
            Items = rows
        };

        return Ok(dto);
    }
}
