namespace Altruist.Database;


public interface IBeforeVaultCreate
{
    Task<bool> BeforeCreateAsync();
}


public interface IOnVaultCreate
{
    Task<List<IVaultModel>> OnCreateAsync();
}

public interface IAfterVaultCreate
{
    Task AfterCreateAsync();
}

public interface IAfterVaultSave
{
    Task AfterSaveAsync();
}

public interface IBeforeVaultSave
{
    Task<bool> BeforeSaveAsync();
}