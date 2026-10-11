/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Persistence;
using Altruist.Persistence.Postgres;

using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace Tests.Altruist.Persistance.Postgres;

/// <summary>SQL the Postgres join layer sends (captured from a mocked provider; no server).</summary>
public sealed class JoinQuerySqlTests : IDisposable
{
    private readonly List<string> _sql = new();
    private readonly ServiceProvider _services;
    private readonly IDisposable _scope;

    public JoinQuerySqlTests()
    {
        var provider = new Mock<ISqlDatabaseProvider>();
        provider.Setup(p => p.QueryAsync<QueryProbe>(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string sql, List<object?>? _, CancellationToken _) => _sql.Add(sql))
            .ReturnsAsync(Array.Empty<QueryProbe>());
        provider.Setup(p => p.ExecuteCountAsync(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string sql, List<object?>? _, CancellationToken _) => _sql.Add(sql))
            .ReturnsAsync(0L);

        var vault = new PgVault<QueryProbe>(provider.Object, new DefaultSchema("we\"ird"), VaultDocument.From(typeof(QueryProbe)));
        _services = new ServiceCollection().AddSingleton<IVault<QueryProbe>>(vault).BuildServiceProvider();
        _scope = Dependencies.PushScope(_services);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
    }

    private static global::Altruist.Querying.IVaultQuery<QueryProbe> From() => new PgVaultQuery().From<QueryProbe>();

    private const string Table = "\"we\"\"ird\".\"query_probe\"";

    [Fact]
    public async Task Filters_order_and_paging_before_Join_select_the_joined_root_rows()
    {
        await From().Where(p => p.Rank > 1).OrderByDescending(p => p.Rank).Take(5)
            .Join<QueryProbe>(a => a.Name, b => b.Tag!)
            .ToListAsync();

        var sql = Assert.Single(_sql);
        Assert.Contains(
            $"FROM (SELECT * FROM {Table} WHERE (\"rank\" > 1) ORDER BY \"rank\" DESC LIMIT 5) AS \"t0\" " +
            $"INNER JOIN {Table} AS \"t1\" ON \"t0\".\"name\" = \"t1\".\"tag\"",
            sql);
        Assert.EndsWith(" ORDER BY \"t0\".\"rank\" DESC", sql);
    }

    [Fact]
    public async Task Join_predicates_compare_null_with_IS_NULL()
    {
        await From().Join<QueryProbe>(a => a.Name, b => b.Name)
            .Where((a, b) => b.Tag == null && a.Score != null)
            .ToListAsync();

        Assert.Contains("WHERE ((\"t1\".\"tag\" IS NULL AND \"t0\".\"score\" IS NOT NULL))", Assert.Single(_sql));
    }

    [Fact]
    public async Task Join_paging_composes_and_count_counts_the_window()
    {
        var q = From().Join<QueryProbe>(a => a.Name, b => b.Name).OrderBy((a, b) => b.Rank).Take(10).Skip(4).Take(3);

        await q.ToListAsync();
        await q.CountAsync();
        await q.FirstOrDefaultAsync();

        Assert.EndsWith(" ORDER BY \"t1\".\"rank\" LIMIT 3 OFFSET 4", _sql[0]);
        Assert.StartsWith("SELECT COUNT(*) FROM (SELECT 1 FROM ", _sql[1]);
        Assert.EndsWith(" LIMIT 3 OFFSET 4) AS q", _sql[1]);
        Assert.EndsWith(" LIMIT 1 OFFSET 4", _sql[2]);
    }

    [Fact]
    public async Task Root_rows_are_read_from_the_root_alias()
    {
        await From().Join<QueryProbe>(a => a.Name, b => b.Name).ToListAsync();
        Assert.StartsWith("SELECT \"t0\".\"name\" AS \"Name\", ", Assert.Single(_sql));
    }

    [Fact]
    public void JoinFrom_rejects_an_ambiguous_model()
    {
        var q = From().Join<QueryProbe>(a => a.Name, b => b.Name);
        Assert.Throws<InvalidOperationException>(() => q.JoinFrom<QueryProbe, QueryProbe>(x => x.Name, y => y.Name));
    }

    [Fact]
    public void Where_after_paging_throws()
    {
        var q = From().Join<QueryProbe>(a => a.Name, b => b.Name).Take(1);
        Assert.Throws<InvalidOperationException>(() => q.Where((a, b) => a.Rank > 1));
    }
}
