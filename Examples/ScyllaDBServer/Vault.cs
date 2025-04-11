using Altruist;
using Altruist.UORM;
using Altruist.Database;

[Vault("player")]
[VaultPrimaryKey(keys: [nameof(Id), nameof(Name)])]
public class SpaceshipPlayer : Spaceship, IOnVaultCreate
{

    public Task<List<IVaultModel>> OnCreateAsync()
    {
        var aPlayer = new SpaceshipPlayer() { Id = "Test", Name = "MyPlayerName" };
        return Task.FromResult(new List<IVaultModel> { aPlayer });
    }
}