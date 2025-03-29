namespace Altruist.Database;


public interface IBeforeVaultCreate
{
    Task<bool> BeforeCreateAsync(IServiceProvider serviceProvider);
}


public interface IOnVaultCreate
{
    Task<List<IVaultModel>> OnCreateAsync(IServiceProvider serviceProvider);
}

public interface IAfterVaultCreate
{
    Task AfterCreateAsync(IServiceProvider serviceProvider);
}

public interface IAfterVaultSave
{
    Task AfterSaveAsync(IServiceProvider serviceProvider);
}

public interface IBeforeVaultSave
{
    Task<bool> BeforeSaveAsync(IServiceProvider serviceProvider);
}