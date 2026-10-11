/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.Extensions.Logging;

using Moq;

namespace Tests.Altruist.Persistance.Postgres;

[Vault("mig_player", Keyspace: "unit_mig")]
public sealed class MigPlayer : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("seen")] public DateTimeOffset Seen { get; set; }
    [VaultColumn("scores")] public int[] Scores { get; set; } = Array.Empty<int>();
    [VaultColumn("tags")] public string[] Tags { get; set; } = Array.Empty<string>();
    [VaultColumn("ratios")] public double[] Ratios { get; set; } = Array.Empty<double>();
    [VaultColumn("note", nullable: true)] public string? Note { get; set; }
}

/// <summary>Same table, explicitly dropping a column whose property is gone.</summary>
[Vault("mig_player", Keyspace: "unit_mig")]
[VaultDropColumn("legacy_score", "replaced by rating")]
public sealed class MigPlayerDropsLegacy : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("seen")] public DateTimeOffset Seen { get; set; }
    [VaultColumn("scores")] public int[] Scores { get; set; } = Array.Empty<int>();
    [VaultColumn("tags")] public string[] Tags { get; set; } = Array.Empty<string>();
    [VaultColumn("ratios")] public double[] Ratios { get; set; } = Array.Empty<double>();
    [VaultColumn("note", nullable: true)] public string? Note { get; set; }
}

[Vault("mig_bad_drop", Keyspace: "unit_mig")]
[VaultDropColumn("name")]
public sealed class MigDropsMappedColumn : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
}

/// <summary>
/// Planner safety: unmapped columns are kept unless dropping is opted into, catalog type spellings do not plan
/// type changes, and the executor dispatches every planned operation (drop table, create schema, relax NOT NULL).
/// </summary>
public sealed class MigrationSafetyTests
{
    private const string Schema = "unit_mig";

    private sealed class ListLogger : ILogger, ILoggerProvider
    {
        public readonly List<string> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add($"{logLevel}: {formatter(state, exception)}");
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
    }

    /// <summary>How information_schema.columns.data_type spelled these types (what the planner used to compare against).</summary>
    private static string CatalogSpelling(string storeType) => storeType switch
    {
        "timestamp" => "timestamp without time zone",
        "timestamptz" => "timestamp with time zone",
        _ when storeType.EndsWith("[]") => "ARRAY",
        _ => storeType,
    };

    /// <summary>The database state right after migrating <typeparamref name="T"/> into an empty schema, with catalog type spellings.</summary>
    private static DatabaseModel ExistingAfterFirstMigration<T>(Func<string, string> spelling, params ColumnModel[] extraColumns)
    {
        var ops = new PostgresMigrationPlanner().Plan(new Dictionary<string, DatabaseModel>(), new[] { VaultDocument.From(typeof(T)) });
        var create = ops.OfType<CreateTableOperation>().Single();

        var columns = create.Columns.ToDictionary(
            c => c.Name,
            c => new ColumnModel(c.Name, spelling(c.StoreType), c.IsNullable, c.DefaultSql is not null),
            StringComparer.OrdinalIgnoreCase);
        foreach (var extra in extraColumns)
            columns[extra.Name] = extra;

        var uniques = create.Columns.Where(c => c.IsUnique)
            .ToDictionary(c => "uq_" + c.Name, c => new UniqueConstraintModel("uq_" + c.Name, new[] { c.Name }), StringComparer.OrdinalIgnoreCase);
        foreach (var uq in ops.OfType<AddUniqueConstraintOperation>())
            uniques[uq.ConstraintName] = new UniqueConstraintModel(uq.ConstraintName, uq.Columns);
        var indexes = ops.OfType<CreateIndexOperation>()
            .ToDictionary(i => i.IndexName, i => new IndexModel(i.IndexName, i.Column), StringComparer.OrdinalIgnoreCase);

        var table = new TableModel(create.Table, columns, create.PrimaryKeyColumns, uniques, indexes, new List<ForeignKeyModel>());
        return new DatabaseModel(Schema, new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase) { [create.Table] = table });
    }

    private static IReadOnlyList<MigrationOperation> Plan<T>(DatabaseModel current, bool dropUnmapped = false, ListLogger? log = null)
    {
        using var factory = LoggerFactory.Create(b => { if (log is not null) b.AddProvider(log); });
        var planner = new PostgresMigrationPlanner(dropUnmapped, factory);
        return planner.Plan(
            new Dictionary<string, DatabaseModel>(StringComparer.OrdinalIgnoreCase) { [Schema] = current },
            new[] { VaultDocument.From(typeof(T)) });
    }

    [Fact]
    public void An_unchanged_model_plans_no_operations_with_catalog_type_spellings()
    {
        // timestamp / timestamptz / arrays as information_schema.data_type reports them.
        Assert.Empty(Plan<MigPlayer>(ExistingAfterFirstMigration<MigPlayer>(CatalogSpelling)));

        // ... and as the inspector now reports them (udt names folded).
        Assert.Empty(Plan<MigPlayer>(ExistingAfterFirstMigration<MigPlayer>(t => PostgresStoreTypes.FromInformationSchema(
            CatalogSpelling(t), t switch { "integer[]" => "_int4", "text[]" => "_text", "double precision[]" => "_float8", _ => null }))));
    }

    [Fact]
    public void A_real_type_change_is_still_planned()
    {
        var current = ExistingAfterFirstMigration<MigPlayer>(t => t == "text" ? "integer" : CatalogSpelling(t));
        var alter = Plan<MigPlayer>(current).OfType<AlterColumnTypeOperation>().ToList();
        Assert.NotEmpty(alter);
        Assert.All(alter, a => Assert.Equal("text", a.NewStoreType));
    }

    [Theory]
    [InlineData("timestamp without time zone", null, "timestamp")]
    [InlineData("timestamp with time zone", null, "timestamptz")]
    [InlineData("ARRAY", "_int4", "integer[]")]
    [InlineData("ARRAY", "_float8", "double precision[]")]
    [InlineData("ARRAY", "_timestamptz", "timestamptz[]")]
    [InlineData("ARRAY", null, "array")]
    [InlineData("character varying", null, "varchar")]
    [InlineData("double precision", null, "double precision")]
    [InlineData("jsonb", null, "jsonb")]
    public void Catalog_types_are_folded_onto_the_type_map(string dataType, string? udt, string expected) =>
        Assert.Equal(expected, PostgresStoreTypes.FromInformationSchema(dataType, udt));

    [Fact]
    public void Unmapped_columns_are_kept_by_default_and_not_null_ones_relaxed()
    {
        var log = new ListLogger();
        var current = ExistingAfterFirstMigration<MigPlayer>(CatalogSpelling,
            new ColumnModel("legacy_score", "integer", isNullable: false, hasDefault: false),
            new ColumnModel("legacy_flag", "boolean", isNullable: false, hasDefault: true),
            new ColumnModel("legacy_note", "text", isNullable: true, hasDefault: false));

        var ops = Plan<MigPlayer>(current, log: log);

        Assert.DoesNotContain(ops, o => o is DropColumnOperation or DeleteMarkedColumnOperation);
        var relax = Assert.Single(ops.OfType<RelaxNotNullOperation>());
        Assert.Equal("legacy_score", relax.ColumnName);
        Assert.Single(ops);

        var warning = Assert.Single(log.Messages, m => m.StartsWith("Warning"));
        Assert.Contains("legacy_flag, legacy_note, legacy_score", warning);
        Assert.Contains("VaultDropColumn", warning);
    }

    [Fact]
    public void Unmapped_columns_are_dropped_only_when_opted_in()
    {
        var current = ExistingAfterFirstMigration<MigPlayer>(CatalogSpelling,
            new ColumnModel("legacy_score", "integer", isNullable: false, hasDefault: false),
            new ColumnModel("legacy_note", "text", isNullable: true, hasDefault: false));

        // Globally (config altruist:persistence:migration:drop-unmapped-columns).
        var global = Plan<MigPlayer>(current, dropUnmapped: true);
        Assert.Equal(new[] { "legacy_note", "legacy_score" },
            global.OfType<DropColumnOperation>().Select(d => d.ColumnName).OrderBy(c => c));
        Assert.DoesNotContain(global, o => o is RelaxNotNullOperation);

        // Per column ([VaultDropColumn] on the class): only the named column goes; the other is kept.
        var perColumn = Plan<MigPlayerDropsLegacy>(current);
        var drop = Assert.Single(perColumn.OfType<DeleteMarkedColumnOperation>());
        Assert.Equal("legacy_score", drop.ColumnName);
        Assert.Equal("replaced by rating", drop.Reason);
        Assert.DoesNotContain(perColumn, o => o is DropColumnOperation);
        Assert.DoesNotContain(perColumn, o => o is RelaxNotNullOperation r && r.ColumnName == "legacy_score");
    }

    [Fact]
    public void VaultDropColumn_cannot_name_a_mapped_column() =>
        Assert.ThrowsAny<Exception>(() => VaultDocument.From(typeof(MigDropsMappedColumn)));

    [Fact]
    public async Task The_executor_dispatches_drop_table_create_schema_and_relax_not_null()
    {
        var sql = new List<string>();
        var provider = new Mock<ISqlDatabaseProvider>();
        provider.Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string s, List<object?>? _, CancellationToken _) => sql.Add(s))
            .ReturnsAsync(0L);
        provider.Setup(p => p.ConnectAsync()).Returns(Task.CompletedTask);

        await new PostgresMigrationExecutor(provider.Object).ApplyAsync("public", new MigrationOperation[]
        {
            new CreateSchemaOperation("game"),
            new DropTableOperation("game", "old_stats"),
            new RelaxNotNullOperation("game", "player", "legacy_score"),
        });

        Assert.Equal(new[]
        {
            "CREATE SCHEMA IF NOT EXISTS \"game\";",
            "DROP TABLE IF EXISTS \"game\".\"old_stats\" CASCADE;",
            "ALTER TABLE \"game\".\"player\" ALTER COLUMN \"legacy_score\" DROP NOT NULL;",
        }, sql);
    }

    [Fact]
    public void Batched_copies_use_ctid_batches_not_UPDATE_LIMIT()
    {
        var stmt = PostgresMigrationExecutor.BuildBatchedCopySql("\"s\".\"t\"", "\"a\"", "\"b\"", "text", 500);
        Assert.StartsWith("UPDATE \"s\".\"t\" SET \"b\" = \"a\"::text WHERE ctid = ANY(ARRAY(SELECT ctid FROM \"s\".\"t\"", stmt);
        Assert.Contains("LIMIT 500))", stmt); // only inside the sub-select
        Assert.EndsWith("AND \"b\" IS NULL AND \"a\" IS NOT NULL;", stmt);
    }
}
