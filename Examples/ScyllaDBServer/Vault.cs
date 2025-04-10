using Altruist;
using Altruist.UORM;
using Altruist.Database;

[Vault("player")]
[VaultPrimaryKey(keys: [nameof(Id), nameof(Name)])]
public class SpaceshipPlayer : Spaceship, IOnVaultLoad
{

    public Task<List<IVaultModel>> OnLoadAsync()
    {
        var aPlayer = new SpaceshipPlayer() { Id = "Test", Name = "MyPlayerName" };
        return Task.FromResult(new List<IVaultModel> { aPlayer });
    }
}