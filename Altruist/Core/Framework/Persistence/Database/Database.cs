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

using Altruist.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Persistence;

public abstract class KeyspaceSetup<TKeyspace> : IKeyspaceSetup where TKeyspace : class, IKeyspace
{
    protected readonly IServiceCollection Services;
    protected readonly List<Type> VaultModels = new();
    protected readonly TKeyspace Instance;

    public IDatabaseServiceToken Token { get; }

    public KeyspaceSetup(IServiceCollection services, TKeyspace instance, IDatabaseServiceToken token)
    {
        Services = services;
        Instance = instance;
        Token = token;
    }

    public abstract Task Build();
}

public interface IKeyspaceSetup
{
    IDatabaseServiceToken Token { get; }
    Task Build();
}


/// <summary>
/// Generic SQL provider contract used by PgVault and the Postgres configuration.
/// Implemented by SqlDbProvider (Npgsql-based).
/// </summary>
public interface ISqlDatabaseProvider : IGeneralDatabaseProvider
{
    Task ConnectAsync(int maxRetries, int delayMilliseconds, CancellationToken ct = default);
    Task ShutdownAsync(Exception? ex = null, CancellationToken ct = default);

    // Query APIs (parameter list aligns with Vaults using "?" placeholders)
    Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        where TVaultModel : class, IVaultModel;

    Task<TVaultModel?> QuerySingleAsync<TVaultModel>(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default)
        where TVaultModel : class, IVaultModel;

    Task<long> ExecuteCountAsync(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default);

    /// <summary>Executes INSERT/UPDATE/DELETE or batched statements; returns affected rows (driver-dependent).</summary>
    Task<long> ExecuteAsync(
        string sql,
        List<object?>? parameters = null,
        CancellationToken ct = default);

    // Optional POCO-based ops (no-ops in current SqlDbProvider; kept for parity with Scylla provider)
    Task<long> UpdateAsync<TVaultModel>(TVaultModel entity, CancellationToken ct = default) where TVaultModel : class, IVaultModel;
    Task<long> DeleteAsync<TVaultModel>(TVaultModel entity, CancellationToken ct = default) where TVaultModel : class, IVaultModel;

    // Bootstrap / DDL
    Task CreateSchemaAsync(string schema, CancellationToken ct = default);
}
