namespace Altruist.Database;


public interface IOnVaultLoad
{
    Task<List<IVaultModel>> OnLoadAsync();
}
