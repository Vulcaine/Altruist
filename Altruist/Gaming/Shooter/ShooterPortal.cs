using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Shooter;


public abstract class AltruistSpaceshipGamePortal : AltruistGamePortal<Spaceship>
{
    public AltruistSpaceshipGamePortal(IPortalContext context, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
    }
}


public abstract class AltruistShootingPortal<TPlayerEntity> : AltruistGamePortal<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    public AltruistShootingPortal(PortalContext context, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
    }

    [Gate("shoot")]
    public async Task HandleShooting(ShootingPacket packet)
    {
        var room = await FindRoomForClientAsync(packet.Header.Sender);
        if (!room.Empty())
        {
            await Router.Room.SendAsync(room.Id, packet);
        }
    }
}
