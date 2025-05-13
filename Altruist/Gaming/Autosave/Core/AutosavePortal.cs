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
using Altruist.Engine;
using Altruist.Persistence;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Autosave;

public interface IAutosaveStrategy { }

public class RealtimeSaveStrategy : IAutosaveStrategy
{
    public CycleRate SaveRate { get; set; }
    public RealtimeSaveStrategy(CycleRate saveRate) => SaveRate = saveRate;
}

public class PeriodicSaveStrategy : IAutosaveStrategy
{
    public string CronExpression { get; set; }
    public PeriodicSaveStrategy(string cronExpression) => CronExpression = cronExpression;
}

/// <summary>
/// Base portal for autosaving game entities into the vault database.
/// Inherit and override <see cref="GetPersistedEntities"/> to define which entity types should be persisted.
/// </summary>
public abstract class AltruistAutosavePortal<TKeyspace> : Portal<GamePortalContext> where TKeyspace : class, IKeyspace, new()
{
    /// <summary>In-memory and external cache interface.</summary>
    protected ICacheProvider Cache { get; }

    /// <summary>Database token used for persistence operations.</summary>
    protected IDatabaseServiceToken Token { get; }

    /// <summary>Repository abstraction over the vault keyed by the current keyspace.</summary>
    protected IVaultRepository<TKeyspace> Repository { get; }

    protected AltruistAutosavePortal(
        GamePortalContext context,
        IDatabaseServiceToken token,
        VaultRepositoryFactory vaultRepository,
        ILoggerFactory loggerFactory)
        : base(context, loggerFactory)
    {
        Cache = context.Cache;
        Token = token;
        Repository = vaultRepository.Make<TKeyspace>();
    }

    /// <summary>
    /// Specifies the types of entities that should be persisted during autosave.
    /// </summary>
    /// <returns>A list of types that implement <see cref="IVaultModel"/>.</returns>
    public abstract List<Type> GetPersistedEntities();

    /// <summary>
    /// Triggers an autosave cycle for all defined entities.
    /// </summary>
    public virtual async Task Save()
    {
        var entities = GetPersistedEntities();
        if (entities.Count == 0) return;

        var tasks = entities.Select(entity => SaveEntityAsync(entity)).ToList();
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Saves all cached entries of the given entity type into the vault.
    /// </summary>
    private async Task SaveEntityAsync(Type entityType)
    {
        if (!typeof(IVaultModel).IsAssignableFrom(entityType))
            return;

        var cursor = await Cache.GetAllAsync(entityType);
        var saveTasks = new List<Task>();

        while (cursor.HasNext)
        {
            var batch = await cursor.NextBatch();
            var models = batch.OfType<IVaultModel>().ToList();

            if (models.Count > 0)
            {
                var vault = Repository.Select(entityType);
                saveTasks.Add(vault.SaveBatchAsync(models));
            }
        }

        await Task.WhenAll(saveTasks);
    }
}

/// <summary>
/// Realtime autosave portal that schedules autosave on a fixed interval.
/// </summary>
public abstract class RealtimeAutosavePortal<TKeyspace> : AltruistAutosavePortal<TKeyspace>
    where TKeyspace : class, IKeyspace, new()
{
    protected RealtimeAutosavePortal(
        GamePortalContext context,
        IDatabaseServiceToken token,
        VaultRepositoryFactory vaultRepository,
        RealtimeSaveStrategy saveStrategy,
        IAltruistEngine engine,
        ILoggerFactory loggerFactory)
        : base(context, token, vaultRepository, loggerFactory)
    {
        engine.ScheduleTask(Save, saveStrategy.SaveRate);
    }
}

/// <summary>
/// Cron-based autosave portal that saves entities on scheduled cron expressions.
/// </summary>
public abstract class PeriodicAutosavePortal<TKeyspace> : AltruistAutosavePortal<TKeyspace>
    where TKeyspace : class, IKeyspace, new()
{
    protected PeriodicAutosavePortal(
        GamePortalContext context,
        IDatabaseServiceToken token,
        VaultRepositoryFactory vaultRepository,
        PeriodicSaveStrategy saveStrategy,
        IAltruistEngine engine,
        ILoggerFactory loggerFactory)
        : base(context, token, vaultRepository, loggerFactory)
    {
        engine.RegisterCronJob(Save, saveStrategy.CronExpression);
    }
}
