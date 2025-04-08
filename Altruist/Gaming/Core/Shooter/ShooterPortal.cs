using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Shooter;


public abstract class AltruistSpaceshipGamePortal : AltruistGamePortal<Spaceship>
{
    public AltruistSpaceshipGamePortal(IPortalContext context, GameWorldCoordinator gameWorldCoordinator, IPlayerService<Spaceship> playerService, ILoggerFactory loggerFactory)
        : base(context, gameWorldCoordinator, playerService, loggerFactory)
    {
    }
}


public abstract class AltruistShootingPortal<TPlayerEntity> : AltruistGamePortal<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    public AltruistShootingPortal(PortalContext context, GameWorldCoordinator gameWorldCoordinator, IPlayerService<TPlayerEntity> playerService, ILoggerFactory loggerFactory)
        : base(context, gameWorldCoordinator, playerService, loggerFactory)
    {
    }

    [Gate("shoot")]
    public async Task HandleShooting(ShootingPacket packet)
    {
        var room = await FindRoomForClientAsync(packet.Header.Sender);
        if (room != null && !room.Empty())
        {
            await Router.Room.SendAsync(room.Id, packet);
        }
    }
}
