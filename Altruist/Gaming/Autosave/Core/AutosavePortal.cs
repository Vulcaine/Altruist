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
using Altruist.Database;
using Altruist.Engine;
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

public abstract class AltruistAutosavePortal<TKeyspace> : Portal<GamePortalContext> where TKeyspace : class, IKeyspace, new()
{
    protected ICacheProvider Cache { get; }
    protected IDatabaseServiceToken Token { get; }

    protected IVaultRepository<TKeyspace> Repository { get; }

    protected AltruistAutosavePortal(GamePortalContext context, IDatabaseServiceToken token, VaultRepositoryFactory vaultRepository, ILoggerFactory loggerFactory)
        : base(context, loggerFactory)
    {
        Cache = context.Cache;
        Token = token;
        Repository = vaultRepository.Make<TKeyspace>();
    }

    public abstract List<Type> GetPersistedEntities();

    public virtual async Task Save()
    {
        var entities = GetPersistedEntities();

        if (entities.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>();
        foreach (var entity in entities)
        {
            tasks.Add(SaveEntity(entity, Repository));
        }

        await Task.WhenAll(tasks);
    }

    private async Task SaveEntity(Type entity, IVaultRepository<TKeyspace> vaultRepository)
    {
        if (entity is not IVaultModel) return;

        var cursor = await Cache.GetAllAsync(entity);
        var saveTasks = new List<Task>();

        while (await cursor.NextBatch())
        {
            var batch = cursor.Items.Where(i => i is IVaultModel).Cast<IVaultModel>().ToList();
            saveTasks.Add(vaultRepository.Select(entity).SaveBatchAsync(batch));
        }

        await Task.WhenAll(saveTasks);
    }
}

public abstract class RealtimeAutosavePortal<TKeyspace> : AltruistAutosavePortal<TKeyspace> where TKeyspace : class, IKeyspace, new()
{
    protected RealtimeAutosavePortal(GamePortalContext context, IDatabaseServiceToken token, VaultRepositoryFactory vaultRepository, RealtimeSaveStrategy saveStrategy, IAltruistEngine engine, ILoggerFactory loggerFactory)
        : base(context, token, vaultRepository, loggerFactory)
    {
        engine.ScheduleTask(Save, saveStrategy.SaveRate);
    }
}

public abstract class PeriodicAutosavePortal<TKeyspace> : AltruistAutosavePortal<TKeyspace> where TKeyspace : class, IKeyspace, new()
{
    protected PeriodicAutosavePortal(GamePortalContext context,
    IDatabaseServiceToken token, VaultRepositoryFactory vaultRepository, PeriodicSaveStrategy saveStrategy, IAltruistEngine engine, ILoggerFactory loggerFactory)
        : base(context, token, vaultRepository, loggerFactory)
    {
        engine.RegisterCronJob(Save, saveStrategy.CronExpression);
    }
}
