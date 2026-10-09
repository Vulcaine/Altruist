/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.RegularExpressions;

using Altruist;
using Altruist.Persistence;
using Altruist.Persistence.Postgres;
using Altruist.UORM;

using Moq;

namespace Tests.Altruist.Persistance.Postgres;

[Vault("query_probe", Keyspace: "unit")]
public sealed class QueryProbe : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("rank")] public int Rank { get; set; }
    [VaultColumn("best")] public int Best { get; set; }
    [VaultColumn("score", nullable: true)] public int? Score { get; set; }
    [VaultColumn("tag", nullable: true)] public string? Tag { get; set; }
    [VaultColumn("at")] public DateTime At { get; set; }

    public string Unmapped { get; set; } = "";
    public QueryProbeOwner Owner { get; set; } = new();
}

public sealed class QueryProbeOwner
{
    public string Name { get; set; } = "";
}

/// <summary>
/// SQL the Postgres vault sends for chains of fluent calls (captured from a mocked provider; no server).
/// </summary>
public sealed class VaultQuerySqlTests
{
    private readonly List<string> _sql = new();
    private readonly PgVault<QueryProbe> _vault;

    public VaultQuerySqlTests()
    {
        var provider = new Mock<ISqlDatabaseProvider>();
        provider.Setup(p => p.QueryAsync<QueryProbe>(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string sql, List<object?>? _, CancellationToken _) => _sql.Add(sql))
            .ReturnsAsync(Array.Empty<QueryProbe>());
        provider.Setup(p => p.ExecuteCountAsync(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string sql, List<object?>? _, CancellationToken _) => _sql.Add(sql))
            .ReturnsAsync(0L);
        provider.Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<List<object?>?>(), It.IsAny<CancellationToken>()))
            .Callback((string sql, List<object?>? _, CancellationToken _) => _sql.Add(sql))
            .ReturnsAsync(0L);
        _vault = new PgVault<QueryProbe>(provider.Object, new DefaultSchema("unit"), VaultDocument.From(typeof(QueryProbe)));
    }

    private async Task<string> ListSql(Func<IVault<QueryProbe>, IVault<QueryProbe>> chain)
    {
        _sql.Clear();
        await chain(_vault).ToListAsync();
        return Assert.Single(_sql);
    }

    private static int Occurrences(string sql, string fragment) => Regex.Matches(sql, Regex.Escape(fragment)).Count;

    // ------------------------------------------------------------------ paging

    [Fact]
    public async Task Take_twice_keeps_the_smaller_window_in_one_limit()
    {
        var sql = await ListSql(v => v.Take(10).Take(3));
        Assert.Equal(1, Occurrences(sql, "LIMIT"));
        Assert.EndsWith(" LIMIT 3", sql);
    }

    [Fact]
    public async Task FirstOrDefault_after_Take_sends_a_single_limit()
    {
        _sql.Clear();
        await _vault.Take(5).FirstOrDefaultAsync();
        var sql = Assert.Single(_sql);
        Assert.Equal(1, Occurrences(sql, "LIMIT"));
        Assert.EndsWith(" LIMIT 1", sql);
    }

    [Fact]
    public async Task FirstOrDefault_after_Take_zero_keeps_the_empty_window()
    {
        _sql.Clear();
        await _vault.Take(0).FirstOrDefaultAsync();
        Assert.EndsWith(" LIMIT 0", Assert.Single(_sql));
    }

    [Fact]
    public async Task Skip_twice_adds_up()
    {
        var sql = await ListSql(v => v.Skip(2).Skip(3));
        Assert.Equal(1, Occurrences(sql, "OFFSET"));
        Assert.EndsWith(" OFFSET 5", sql);
    }

    [Fact]
    public async Task Skip_after_Take_pages_inside_the_taken_window()
    {
        Assert.EndsWith(" LIMIT 6 OFFSET 4", await ListSql(v => v.Take(10).Skip(4)));
        Assert.EndsWith(" LIMIT 0 OFFSET 12", await ListSql(v => v.Take(10).Skip(12)));
        Assert.EndsWith(" LIMIT 3 OFFSET 4", await ListSql(v => v.Skip(4).Take(3)));
    }

    [Fact]
    public async Task Negative_counts_are_treated_as_zero()
    {
        Assert.EndsWith(" LIMIT 0", await ListSql(v => v.Take(-1)));
        Assert.DoesNotContain("OFFSET", await ListSql(v => v.Skip(-5)));
    }

    [Fact]
    public void Where_and_OrderBy_after_paging_throw_instead_of_filtering_before_the_page()
    {
        Assert.Throws<InvalidOperationException>(() => _vault.Take(10).Where(p => p.Rank > 1));
        Assert.Throws<InvalidOperationException>(() => _vault.Skip(1).OrderBy(p => p.Rank));
        Assert.Throws<InvalidOperationException>(() => _vault.Take(1).OrderByDescending(p => p.Rank));
    }

    // ------------------------------------------------------------------ ordering

    [Fact]
    public async Task Sort_keys_keep_their_call_order()
    {
        var sql = await ListSql(v => v.OrderBy(p => p.Rank).OrderByDescending(p => p.Name).OrderBy(p => p.Best)
            .OrderByDescending(p => p.At).OrderBy(p => p.Tag));
        Assert.EndsWith(" ORDER BY \"rank\", \"name\" DESC, \"best\", \"at\" DESC, \"tag\"", sql);
    }

    // ------------------------------------------------------------------ count

    [Fact]
    public async Task Count_of_a_paged_chain_counts_the_window()
    {
        _sql.Clear();
        await _vault.Where(p => p.Rank > 1).OrderBy(p => p.Rank).Skip(2).Take(3).CountAsync();
        var sql = Assert.Single(_sql);
        Assert.StartsWith("SELECT COUNT(*) FROM (SELECT 1 FROM \"unit\".\"query_probe\" WHERE (\"rank\" > 1) ORDER BY \"rank\" LIMIT 3 OFFSET 2) AS q", sql);
    }

    [Fact]
    public async Task Count_of_an_unpaged_chain_counts_the_filtered_table()
    {
        _sql.Clear();
        await _vault.Where(p => p.Rank > 1).CountAsync();
        Assert.Equal("SELECT COUNT(*) FROM \"unit\".\"query_probe\" WHERE (\"rank\" > 1)", Assert.Single(_sql));
    }

    // ------------------------------------------------------------------ select

    [Fact]
    public async Task SelectAsync_after_Where_OrderBy_Take_selects_only_the_projection()
    {
        _sql.Clear();
        await _vault.Where(p => p.Rank > 1).OrderBy(p => p.Rank).Take(5)
            .SelectAsync(p => new QueryProbe { StorageId = p.StorageId, Name = p.Name });
        var sql = Assert.Single(_sql);
        Assert.StartsWith("SELECT \"id\" AS \"StorageId\", \"name\" AS \"Name\" FROM \"unit\".\"query_probe\"", sql);
        Assert.EndsWith(" WHERE (\"rank\" > 1) ORDER BY \"rank\" LIMIT 5", sql);
    }

    [Fact]
    public async Task SelectAsync_aliases_the_assigned_member_to_the_source_column()
    {
        _sql.Clear();
        await _vault.SelectAsync(p => new QueryProbe { Best = p.Rank });
        Assert.StartsWith("SELECT \"rank\" AS \"Best\" FROM", Assert.Single(_sql));
    }

    // ------------------------------------------------------------------ delete

    [Fact]
    public async Task Delete_without_a_filter_throws_instead_of_emptying_the_table()
    {
        _sql.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vault.DeleteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vault.OrderBy(p => p.Rank).DeleteAsync());
        Assert.Empty(_sql);
    }

    [Fact]
    public async Task DeleteAll_empties_the_table_explicitly()
    {
        _sql.Clear();
        await _vault.Where(p => p.Rank > 1).DeleteAllAsync();
        Assert.Equal("DELETE FROM \"unit\".\"query_probe\"", Assert.Single(_sql));
    }

    [Fact]
    public async Task Delete_of_a_paged_chain_deletes_the_window_by_primary_key()
    {
        _sql.Clear();
        await _vault.Where(p => p.Rank > 1).OrderBy(p => p.Rank).Take(2).DeleteAsync();
        Assert.Equal(
            "DELETE FROM \"unit\".\"query_probe\" WHERE (\"id\") IN (SELECT \"id\" FROM \"unit\".\"query_probe\" WHERE (\"rank\" > 1) ORDER BY \"rank\" LIMIT 2)",
            Assert.Single(_sql));
    }

    // ------------------------------------------------------------------ predicates

    private async Task<string> WhereSql(System.Linq.Expressions.Expression<Func<QueryProbe, bool>> predicate)
    {
        var sql = await ListSql(v => v.Where(predicate));
        return sql[(sql.IndexOf(" WHERE ", StringComparison.Ordinal) + 7)..];
    }

    [Fact]
    public async Task Null_comparisons_follow_csharp_semantics()
    {
        int? none = null;
        Assert.Equal("(\"score\" IS NULL)", await WhereSql(p => p.Score == null));
        Assert.Equal("(\"score\" IS NOT NULL)", await WhereSql(p => p.Score != none));
        // Lifted comparisons with null are false in C#; they used to become IS NOT NULL.
        Assert.Equal("(FALSE)", await WhereSql(p => p.Score > none));
        Assert.Equal("(FALSE)", await WhereSql(p => none <= p.Score));
    }

    [Fact]
    public async Task Not_equal_to_a_value_keeps_null_rows_like_csharp()
    {
        Assert.Equal("(\"tag\" IS DISTINCT FROM E'a')", await WhereSql(p => p.Tag != "a"));
    }

    [Fact]
    public async Task Two_columns_of_the_row_can_be_compared()
    {
        Assert.Equal("(\"rank\" < \"best\")", await WhereSql(p => p.Rank < p.Best));
        Assert.Equal("(\"rank\" IS NOT DISTINCT FROM \"best\")", await WhereSql(p => p.Rank == p.Best));
        Assert.Equal("(\"best\" > 3)", await WhereSql(p => 3 < p.Best));
    }

    [Fact]
    public async Task Nullable_Value_reads_the_column()
    {
        Assert.Equal("(\"score\" >= 10)", await WhereSql(p => p.Score!.Value >= 10));
    }

    [Fact]
    public async Task Nested_members_and_unmapped_properties_are_rejected()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => WhereSql(p => p.Owner.Name == "x"));
        await Assert.ThrowsAsync<NotSupportedException>(() => WhereSql(p => p.Unmapped == "x"));
        await Assert.ThrowsAsync<NotSupportedException>(() => ListSql(v => v.OrderBy(p => p.Unmapped)));
    }

    [Fact]
    public async Task Local_DateTime_values_are_converted_to_utc()
    {
        var local = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Local);
        var utc = local.ToUniversalTime();
        Assert.Equal($"(\"at\" = '{utc:yyyy-MM-dd HH:mm:ss}.000000')", await WhereSql(p => p.At == local));
    }

    [Fact]
    public async Task Captured_value_comparisons_become_constants()
    {
        var flag = true;
        Assert.Equal("((TRUE AND \"rank\" = 1))", await WhereSql(p => flag == true && p.Rank == 1));
    }

    // ------------------------------------------------------------------ cursor

    [Fact]
    public async Task Cursor_pages_an_unpaged_chain_in_primary_key_order()
    {
        _sql.Clear();
        var cursor = await _vault.Where(p => p.Rank > 1).OrderBy(p => p.Name).ToCursorAsync();
        Assert.True(cursor.HasNext);
        Assert.Empty(await cursor.NextBatch());
        Assert.False(cursor.HasNext);

        var sql = Assert.Single(_sql);
        Assert.EndsWith($" WHERE (\"rank\" > 1) ORDER BY \"name\", \"id\" LIMIT {SqlVault<QueryProbe>.CursorBatchSize}", sql);
    }
}
