/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Data;
using System.Data.Common;

using Altruist.Persistence;

using Npgsql;

namespace Tests.Altruist.Persistance;

/// <summary>
/// Unit tests of the AsyncLocal binding behind SqlAmbientTransaction (no server needed). The
/// database-level behaviour (commit, rollback, nesting) is covered by the Postgres integration tests.
/// </summary>
public sealed class AmbientTransactionTests
{
    private sealed class FakeTransaction : DbTransaction
    {
        private readonly DbConnection _connection;
        public FakeTransaction(DbConnection connection) => _connection = connection;
        public int Commits, Rollbacks;
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => _connection;
        public override void Commit() => Commits++;
        public override void Rollback() => Rollbacks++;
    }

    private static (DbConnection, FakeTransaction) NewPair()
    {
        var conn = new NpgsqlConnection();
        return (conn, new FakeTransaction(conn));
    }

    [Fact]
    public void No_transaction_is_active_by_default()
    {
        Assert.Null(SqlAmbientTransaction.Current);
    }

    [Fact]
    public void Enter_binds_and_dispose_restores_the_previous_binding()
    {
        var (c1, t1) = NewPair();
        var (c2, t2) = NewPair();

        using (SqlAmbientTransaction.Enter(c1, t1))
        {
            Assert.Same(t1, SqlAmbientTransaction.Current!.Transaction);
            using (SqlAmbientTransaction.Enter(c2, t2))
            {
                Assert.Same(c2, SqlAmbientTransaction.Current!.Connection);
            }
            Assert.Same(t1, SqlAmbientTransaction.Current!.Transaction);
        }
        Assert.Null(SqlAmbientTransaction.Current);
    }

    [Fact]
    public void Disposing_the_binding_twice_is_harmless_and_never_commits_or_rolls_back()
    {
        var (c, t) = NewPair();
        var binding = SqlAmbientTransaction.Enter(c, t);
        binding.Dispose();
        binding.Dispose();
        Assert.Null(SqlAmbientTransaction.Current);
        Assert.Equal(0, t.Commits);
        Assert.Equal(0, t.Rollbacks);
    }

    [Fact]
    public async Task Binding_flows_into_awaited_calls_but_not_back_out_of_them()
    {
        var (c, t) = NewPair();
        using (SqlAmbientTransaction.Enter(c, t))
        {
            await Task.Yield();
            Assert.Same(t, SqlAmbientTransaction.Current!.Transaction);
            Assert.Same(t, await Task.Run(() => SqlAmbientTransaction.Current!.Transaction));
        }

        // An AsyncLocal set inside an awaited method is invisible to the caller afterwards: this is
        // why the API is callback based (InTransactionAsync) and not a Begin...Async returning a scope.
        async Task EnterInside()
        {
            await Task.Yield();
            SqlAmbientTransaction.Enter(c, t);
        }
        await EnterInside();
        Assert.Null(SqlAmbientTransaction.Current);
    }

    [Fact]
    public void Arguments_are_validated()
    {
        var (c, t) = NewPair();
        Assert.Throws<ArgumentNullException>(() => SqlAmbientTransaction.Enter(null!, t));
        Assert.Throws<ArgumentNullException>(() => SqlAmbientTransaction.Enter(c, null!));
    }
}
