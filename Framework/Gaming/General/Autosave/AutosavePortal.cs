using Altruist.Database;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

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

public abstract class AltruistAutosavePortal<TKeyspace> : Portal where TKeyspace : class, IKeyspace
{
    protected ICache Cache { get; }
    protected AltruistAutosavePortal(IPortalContext context, ILoggerFactory loggerFactory)
        : base(context, loggerFactory)
    {
        Cache = context.Cache;
    }

    public abstract List<Type> GetPersistedEntities();

    public virtual async Task Save(IVaultRepository<TKeyspace> vaultRepository)
    {
        var entities = GetPersistedEntities();

        if (entities.Count == 0)
        {
            return;
        }

        var tasks = new List<Task>();
        foreach (var entity in entities)
        {
            tasks.Add(SaveEntity(entity, vaultRepository));
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

public abstract class RealtimeAutosavePortal<TKeyspace> : AltruistAutosavePortal<TKeyspace> where TKeyspace : class, IKeyspace
{
    protected RealtimeAutosavePortal(IPortalContext context, RealtimeSaveStrategy saveStrategy, IAltruistEngine engine, ILoggerFactory loggerFactory)
        : base(context, loggerFactory)
    {
        engine.ScheduleTask(Save, saveStrategy.SaveRate);
    }
}

public abstract class PeriodicAutosavePortal<TKeyspace> : AltruistAutosavePortal<TKeyspace> where TKeyspace : class, IKeyspace
{
    protected PeriodicAutosavePortal(IPortalContext context, PeriodicSaveStrategy saveStrategy, IAltruistEngine engine, ILoggerFactory loggerFactory)
        : base(context, loggerFactory)
    {
        engine.RegisterCronJob(Save, saveStrategy.CronExpression);
    }
}
