using Altruist;
using Altruist.UORM;

[Vault("player")]
[VaultPrimaryKey(keys: [nameof(Id), nameof(Name)])]
public class SpaceshipPlayer : Spaceship
{

    public override Task<List<IVaultModel>> PreLoad()
    {
        var aPlayer = new SpaceshipPlayer() { Id = "Test", Name = "MyPlayerName" };
        return Task.FromResult(new List<IVaultModel> { aPlayer });
    }
}