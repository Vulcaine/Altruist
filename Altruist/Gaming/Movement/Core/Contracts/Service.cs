namespace Altruist.Gaming.Movement;

public interface IMovementService<TPlayerEntity> where TPlayerEntity : PlayerEntity
{
    Task<TPlayerEntity?> MovePlayerAsync(string playerId, IMovementPacket input);
}
