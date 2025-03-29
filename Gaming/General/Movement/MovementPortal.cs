using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

public abstract class AltruistMovementPortal<TPlayerEntity, TMovementInput> : Portal
    where TPlayerEntity : PlayerEntity
    where TMovementInput : MovementInput
{
    private readonly IPlayerService<TPlayerEntity> _playerService;
    private readonly IMovementService<TPlayerEntity, TMovementInput> _movementService;

    protected AltruistMovementPortal(PortalContext context, IPlayerService<TPlayerEntity> playerService, IMovementService<TPlayerEntity, TMovementInput> movementService, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
        _playerService = playerService;
        _movementService = movementService;
    }

    [Gate("sync-movement")]
    public async virtual Task SyncMovement(IMovementPacket movement, string clientId)
    {
        var player = await _playerService.GetPlayerAsync(movement.Header.Sender);
        if (player == null) return;

        var movementInput = CreateMovementInput(movement);
        var updatedEntity = await _movementService.MovePlayerAsync(movement.Header.Sender, movementInput);

        if (updatedEntity != null)
        {
            await Router.Synchronize.SendAsync(updatedEntity);
        }
    }

    protected abstract TMovementInput CreateMovementInput(IMovementPacket movement);
}

public abstract class AltruistForwardMovementPortal<TMovementInput> : AltruistMovementPortal<TMovementInput, ForwardMovementInput> where TMovementInput : PlayerEntity
{
    public AltruistForwardMovementPortal(PortalContext context, IPlayerService<TMovementInput> playerService, IMovementService<TMovementInput, ForwardMovementInput> movementService, ILoggerFactory loggerFactory)
        : base(context, playerService, movementService, loggerFactory) { }

    protected override ForwardMovementInput CreateMovementInput(IMovementPacket movement)
    {
        if (movement is ForwardMovementPacket forwardMovementPacket)
        {
            return new ForwardMovementInput
            {
                RotateLeft = forwardMovementPacket.RotateLeft,
                RotateRight = forwardMovementPacket.RotateRight,
                MoveUp = forwardMovementPacket.MoveUp,
                Turbo = forwardMovementPacket.Turbo
            };
        }

        throw new InvalidCastException("Invalid movement packet type.");
    }

}

public abstract class AltruistEightDirectionMovementPortal<TMovementInput> : AltruistMovementPortal<TMovementInput, EightDirectionMovementInput> where TMovementInput : PlayerEntity
{
    public AltruistEightDirectionMovementPortal(PortalContext context, IPlayerService<TMovementInput> playerService, IMovementService<TMovementInput, EightDirectionMovementInput> movementService, ILoggerFactory loggerFactory)
        : base(context, playerService, movementService, loggerFactory) { }

    protected override EightDirectionMovementInput CreateMovementInput(IMovementPacket movement)
    {
        if (movement is EightDirectionMovementPacket eightDirectionPacket)
        {
            return new EightDirectionMovementInput
            {
                MoveUp = eightDirectionPacket.MoveUp,
                MoveDown = eightDirectionPacket.MoveDown,
                MoveLeft = eightDirectionPacket.MoveLeft,
                MoveRight = eightDirectionPacket.MoveRight,
                Turbo = eightDirectionPacket.Turbo
            };
        }

        throw new InvalidCastException("Invalid movement packet type.");
    }
}