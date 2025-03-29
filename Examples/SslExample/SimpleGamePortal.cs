using Altruist;
using Altruist.Database;
using Altruist.Gaming;
using Altruist.UORM;
using Microsoft.Extensions.Logging;

namespace Portals;

public class SimpleGamePortal : AltruistGameSessionPortal<SpaceshipPlayer>
{
    public SimpleGamePortal(IPortalContext context, GameWorldCoordinator gameWorld, IPlayerService<SpaceshipPlayer> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, playerService, loggerFactory)
    {
    }
}


[Vault("player")]
[VaultPrimaryKey(keys: [nameof(GenId), nameof(Name)])]
public class SpaceshipPlayer : Spaceship, IOnVaultCreate
{
    public Task<List<IVaultModel>> OnCreateAsync(IServiceProvider serviceProvider)
    {
        var aPlayer = new SpaceshipPlayer() { GenId = "Test", Name = "MyPlayerName" };
        return Task.FromResult(new List<IVaultModel> { aPlayer });
    }
}