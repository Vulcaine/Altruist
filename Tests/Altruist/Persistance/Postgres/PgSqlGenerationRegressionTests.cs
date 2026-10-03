/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

using Altruist;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.Persistence.Postgres;
using Altruist.UORM;

using Moq;

using Npgsql;

namespace Tests.Altruist.Persistance.Postgres;

public enum ProbeKind : long
{
    None = 0,
    Large = 5_000_000_000,
}

[Vault("literal_probe", Keyspace: "unit")]
public sealed class LiteralProbe : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("rank")] public int Rank { get; set; }
    [VaultColumn("ratio")] public double Ratio { get; set; }
    [VaultColumn("amount")] public decimal Amount { get; set; }
    [VaultColumn("at")] public DateTime At { get; set; }
    [VaultColumn("seen")] public DateTimeOffset Seen { get; set; }
    [VaultColumn("ref")] public Guid Ref { get; set; }
    [VaultColumn("kind")] public ProbeKind Kind { get; set; }
    [VaultColumn("initial")] public char Initial { get; set; }
}

/// <summary>
/// SQL text produced by PgVault / PgQueryTranslator / PgJoinExpressionTranslator (inlined
/// literals: no server needed). Regressions: OrderBy replaced the projection with the sort
/// column; literals were culture-dependent, truncated timestamps to seconds, could be terminated
/// by a backslash (standard_conforming_strings=off) and emitted ToString() output raw.
/// </summary>
public sealed class PgQueryTranslatorRegressionTests
{
    private readonly List<string> _sql = new();
    private readonly PgVault<LiteralProbe> _vault;

    public PgQueryTranslatorRegressionTests()
    {
        var provider = new Mock<ISqlDatabaseProvider>();
        provider.Setup(p => p.QueryAsync<LiteralProbe>(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string sql, List<object?>? _, CancellationToken _) => _sql.Add(sql))
            .ReturnsAsync(Array.Empty<LiteralProbe>());
        _vault = new PgVault<LiteralProbe>(provider.Object, new DefaultSchema("unit"), VaultDocument.From(typeof(LiteralProbe)));
    }

    private async Task<string> SqlOf(Func<IVault<LiteralProbe>, IVault<LiteralProbe>> query)
    {
        _sql.Clear();
        await query(_vault).ToListAsync();
        return Assert.Single(_sql);
    }

    private static string WithCulture(string culture, Func<string> body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try { return body(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task OrderBy_keeps_the_full_projection()
    {
        var sql = await SqlOf(v => v.OrderBy(p => p.Rank).Take(5));
        Assert.Contains("\"name\" AS \"Name\"", sql);
        Assert.Contains("\"ratio\" AS \"Ratio\"", sql);
        Assert.Contains("ORDER BY", sql);

        var desc = await SqlOf(v => v.OrderByDescending(p => p.Rank).Where(p => p.Rank > 1));
        Assert.Contains("\"name\" AS \"Name\"", desc);
        Assert.Contains("DESC", desc);
    }

    [Fact]
    public async Task String_literals_escape_quotes_and_backslashes_with_escape_syntax()
    {
        var name = "it's\\";
        var sql = await SqlOf(v => v.Where(p => p.Name == name));
        Assert.Contains("E'it''s\\\\'", sql);
    }

    [Fact]
    public async Task Strings_with_NUL_are_rejected()
    {
        var name = "nul\0byte";
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SqlOf(v => v.Where(p => p.Name == name)));
    }

    [Fact]
    public async Task DateTime_literals_keep_microseconds_in_every_culture()
    {
        var at = new DateTime(2024, 5, 6, 7, 8, 9).AddTicks(1234567);
        var sql = WithCulture("ar-SA", () => SqlOf(v => v.Where(p => p.At == at)).GetAwaiter().GetResult());
        Assert.Contains("'2024-05-06 07:08:09.123456'", sql);
    }

    [Fact]
    public async Task DateTimeOffset_literals_are_normalized_to_UTC()
    {
        var seen = new DateTimeOffset(2024, 1, 2, 12, 0, 0, TimeSpan.FromHours(2));
        var sql = await SqlOf(v => v.Where(p => p.Seen == seen));
        Assert.Contains("'2024-01-02 10:00:00.000000+00'", sql);
    }

    [Fact]
    public void Numeric_literals_are_culture_invariant()
    {
        var ratio = 1.5;
        var amount = 1234.5m;
        var sql = WithCulture("de-DE", () => SqlOf(v => v.Where(p => p.Ratio == ratio && p.Amount == amount)).GetAwaiter().GetResult());
        Assert.Contains("1.5", sql);
        Assert.Contains("1234.5", sql);
        Assert.DoesNotContain("1,5", sql);
    }

    [Fact]
    public async Task Guid_literals_are_quoted_and_long_backed_enums_do_not_overflow()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var kind = ProbeKind.Large;
        var sql = await SqlOf(v => v.Where(p => p.Ref == id && p.Kind == kind));
        Assert.Contains("'0f8fad5b-d9cb-469f-a165-70867728950e'", sql);
        Assert.Contains("5000000000", sql);
    }

    // ------------------------------------------------------------------ join translator

    private static string TranslateJoinPredicate<T>(Expression<Func<T, bool>> predicate, object vault)
    {
        var translator = typeof(PgSqlDbProvider).Assembly.GetType("Altruist.Persistence.Postgres.Querying.PgJoinExpressionTranslator", throwOnError: true)!;
        var translate = translator.GetMethod("Translate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var map = new Dictionary<ParameterExpression, object> { [predicate.Parameters[0]] = vault };
        try
        {
            return (string)translate.Invoke(null, new object[] { predicate, map })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public void Join_predicates_quote_guids_and_escape_strings()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var name = "o'brien\\";
        var sql = TranslateJoinPredicate<LiteralProbe>(p => p.Ref == id && p.Name == name, _vault);
        Assert.Contains("'0f8fad5b-d9cb-469f-a165-70867728950e'", sql);
        Assert.Contains("E'o''brien\\\\'", sql);
    }

    [Fact]
    public void Join_predicates_are_culture_invariant_and_keep_microseconds()
    {
        var ratio = 2.25;
        var at = new DateTime(2024, 5, 6, 7, 8, 9).AddTicks(1234560);
        var sql = WithCulture("fr-FR", () => TranslateJoinPredicate<LiteralProbe>(p => p.Ratio == ratio && p.At == at, _vault));
        Assert.Contains("2.25", sql);
        Assert.Contains("'2024-05-06 07:08:09.123456'", sql);
    }

    [Fact]
    public void Join_predicates_reject_NUL()
    {
        var name = "a\0b";
        Assert.ThrowsAny<ArgumentException>(() => TranslateJoinPredicate<LiteralProbe>(p => p.Name == name, _vault));
    }
}

/// <summary>Connection strings built by the Postgres providers pin the session time zone to UTC.</summary>
public sealed class PgProviderConnectionStringTests
{
    [Fact]
    public void Default_provider_sets_utc_timezone()
    {
        var provider = new PgSqlDbProvider(new JsonSerializerOptions(), "localhost", 5432, "user", "pw", "db");
        Assert.Equal("UTC", new NpgsqlConnectionStringBuilder(provider.GetConnectionString()).Timezone);
    }

    [Fact]
    public void Named_instance_provider_sets_utc_timezone()
    {
        var provider = new PgSqlDbInstanceProvider(new JsonSerializerOptions(), "replica", "localhost", 5432, "user", "pw", "db");
        Assert.Equal("UTC", new NpgsqlConnectionStringBuilder(provider.GetConnectionString()).Timezone);
    }
}

/// <summary>SQL emitted by PostgresMigrationExecutor (captured; no server).</summary>
public sealed class PgMigrationExecutorSqlTests
{
    private readonly List<string> _sql = new();
    private readonly Mock<ISqlDatabaseProvider> _provider = new();

    public PgMigrationExecutorSqlTests()
    {
        _provider.Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string sql, List<object?>? _, CancellationToken _) => _sql.Add(sql))
            .ReturnsAsync(0L);
    }

    [Fact]
    public async Task Create_index_uses_an_unqualified_index_name()
    {
        await new PostgresMigrationExecutor(_provider.Object).ApplyAsync("s",
            new MigrationOperation[] { new CreateIndexOperation("s", "t", "ix_t_owner", "owner") });

        // "CREATE INDEX "s"."ix" ..." is a syntax error in Postgres: the index always lives in the table's schema.
        var sql = Assert.Single(_sql, s => s.Contains("CREATE INDEX"));
        Assert.Equal("CREATE INDEX IF NOT EXISTS \"ix_t_owner\" ON \"s\".\"t\" (\"owner\");", sql);
    }

    [Fact]
    public async Task Operations_run_through_the_providers_transaction_not_BEGIN_COMMIT_statements()
    {
        var tx = _provider.As<ISqlTransactionProvider>();
        var inTransaction = false;
        tx.Setup(t => t.InTransactionAsync(It.IsAny<Func<CancellationToken, Task<bool>>>(), It.IsAny<System.Data.IsolationLevel>(), It.IsAny<CancellationToken>()))
            .Returns(async (Func<CancellationToken, Task<bool>> work, System.Data.IsolationLevel _, CancellationToken ct) =>
            {
                inTransaction = true;
                try { return await work(ct); }
                finally { inTransaction = false; }
            });
        var executedInTx = new List<bool>();
        _provider.Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string sql, List<object?>? _, CancellationToken _) => { _sql.Add(sql); executedInTx.Add(inTransaction); })
            .ReturnsAsync(0L);

        await new PostgresMigrationExecutor(_provider.Object).ApplyAsync("s", new MigrationOperation[]
        {
            new CreateIndexOperation("s", "t", "ix_a", "a"),
            new CreateIndexOperation("s", "t", "ix_b", "b"),
        });

        tx.Verify(t => t.InTransactionAsync(It.IsAny<Func<CancellationToken, Task<bool>>>(), It.IsAny<System.Data.IsolationLevel>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain(_sql, s => s.Trim().TrimEnd(';').Equals("BEGIN", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_sql, s => s.Trim().TrimEnd(';').Equals("COMMIT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, executedInTx.Count);
        Assert.All(executedInTx, Assert.True);
    }

    [Fact]
    public async Task A_failing_operation_is_reported_with_its_position()
    {
        _provider.Setup(p => p.ExecuteAsync(It.Is<string>(s => s.Contains("ix_bad")), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bad index"));

        var ex = await Assert.ThrowsAsync<MigrationException>(() => new PostgresMigrationExecutor(_provider.Object).ApplyAsync("s",
            new MigrationOperation[]
            {
                new CreateIndexOperation("s", "t", "ix_ok", "a"),
                new CreateIndexOperation("s", "t", "ix_bad", "b"),
            }));
        Assert.Contains("operation 2/2", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}

/// <summary>
/// Adding a single-column unique key together with its new column (ADD COLUMN ... UNIQUE) must
/// not also plan an AddUniqueConstraintOperation for the same column (two identical constraints).
/// </summary>
public sealed class MigrationPlannerUniqueColumnTests
{
    [Vault("unique_plan", Keyspace: "unit")]
    [VaultUniqueKey(nameof(Name))]
    public sealed class PlanV1 : VaultModel
    {
        [VaultColumn("name")] public string Name { get; set; } = "";
    }

    [Vault("unique_plan", Keyspace: "unit")]
    [VaultUniqueKey(nameof(Name))]
    [VaultUniqueKey(nameof(Email))]
    [VaultUniqueKey(nameof(Name), nameof(Region))]
    public sealed class PlanV2 : VaultModel
    {
        [VaultColumn("name")] public string Name { get; set; } = "";
        [VaultColumn("email", nullable: true)] public string? Email { get; set; }
        [VaultColumn("region", nullable: true)] public string? Region { get; set; }
    }

    private static DatabaseModel ExistingV1(PostgresMigrationPlanner planner)
    {
        // Plan V1 against an empty schema and turn the CREATE TABLE into the "current" model.
        var create = planner.Plan(new Dictionary<string, DatabaseModel>(), new[] { VaultDocument.From(typeof(PlanV1)) })
            .OfType<CreateTableOperation>().Single();
        var table = new TableModel(
            create.Table,
            create.Columns.ToDictionary(c => c.Name, c => new ColumnModel(c.Name, c.StoreType, c.IsNullable), StringComparer.OrdinalIgnoreCase),
            create.PrimaryKeyColumns,
            new Dictionary<string, UniqueConstraintModel> { ["unique_plan_name_key"] = new("unique_plan_name_key", new[] { "name" }) },
            new Dictionary<string, IndexModel>(),
            Array.Empty<ForeignKeyModel>());
        return new DatabaseModel(create.Schema, new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase) { [create.Table] = table });
    }

    [Fact]
    public void New_unique_column_is_created_inline_without_a_second_constraint()
    {
        var planner = new PostgresMigrationPlanner();
        var current = ExistingV1(planner);

        var ops = planner.Plan(new Dictionary<string, DatabaseModel>(StringComparer.OrdinalIgnoreCase) { [current.Schema] = current },
            new[] { VaultDocument.From(typeof(PlanV2)) });

        var addEmail = ops.OfType<AddColumnOperation>().Single(o => o.Column.Name == "email");
        Assert.True(addEmail.Column.IsUnique);
        Assert.DoesNotContain(ops.OfType<AddUniqueConstraintOperation>(), o => o.Columns.Count == 1 && o.Columns[0] == "email");

        // Composite keys that include a new column are still added as constraints.
        Assert.Contains(ops.OfType<AddUniqueConstraintOperation>(),
            o => o.Columns.OrderBy(c => c).SequenceEqual(new[] { "name", "region" }));
        // The existing single-column key is left alone.
        Assert.DoesNotContain(ops.OfType<AddUniqueConstraintOperation>(), o => o.Columns.Count == 1 && o.Columns[0] == "name");
        Assert.DoesNotContain(ops.OfType<DropConstraintOperation>(), _ => true);
    }
}
