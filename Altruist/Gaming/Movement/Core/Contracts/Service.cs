namespace Altruist.Gaming.Movement;

public interface IMovementService
{
    Task<PlayerEntity?> MovePlayerAsync(string playerId, IMovementPacket input);
}
