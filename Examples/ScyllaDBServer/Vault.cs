using Altruist;
using Altruist.Security;
using Altruist.Database;
using Altruist.UORM;

[Vault("player")]
[VaultPrimaryKey(keys: [nameof(GenId), nameof(Name)])]
public class SpaceshipPlayer : Spaceship, IOnVaultCreate
{
    public Task<List<IVaultModel>> OnCreateAsync()
    {
        var aPlayer = new SpaceshipPlayer() { GenId = "Test", Name = "MyPlayerName" };
        return Task.FromResult(new List<IVaultModel> { aPlayer });
    }
}

[Vault("account")]
[VaultPrimaryKey(keys: [nameof(Username)])]
public class MyAccount : UsernamePasswordAccountVault, IOnVaultCreate
{
    public Task<List<IVaultModel>> OnCreateAsync()
    {
        var adminAccount = new MyAccount { Username = "AltruistAdmin", PasswordHash = "someHashedPass" };
        return Task.FromResult(new List<IVaultModel> { adminAccount });
    }
}