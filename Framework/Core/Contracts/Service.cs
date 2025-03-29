namespace Altruist;

public interface IPlayerService<TPlayerEntity> : ICleanUp where TPlayerEntity : PlayerEntity
{
    Task<Player> ConnectById(string roomId, string socketId, string name);
    Task<TPlayerEntity?> FindEntityAsync(string playerId);
    Task UpdatePlayerAsync(Player player);
    Task DisconnectAsync(string socketId);
    Task DeletePlayerAsync(string playerId);
    Task<Player?> GetPlayerAsync(string playerId);
}


public interface IMovementService<TPlayerEntity, TMovementInput> where TPlayerEntity : PlayerEntity where TMovementInput : MovementInput
{
    Task<TPlayerEntity?> MovePlayerAsync(string playerId, TMovementInput input);
}

public interface IRelayService
{
    string RelayEvent { get; }
    Task Relay(IPacket data);
    Task ConnectAsync();
}

public abstract class AbstractRelayService : IRelayService
{
    public abstract string RelayEvent { get; }

    public abstract Task ConnectAsync();
    public abstract Task Relay(IPacket data);
}