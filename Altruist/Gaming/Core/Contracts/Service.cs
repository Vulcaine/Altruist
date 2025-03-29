namespace Altruist.Gaming;

public interface IPlayerService<TPlayerEntity> : ICleanUp where TPlayerEntity : PlayerEntity, new()
{
    Task<TPlayerEntity?> ConnectById(string roomId, string socketId, string name, float[]? positon = null);
    Task<TPlayerEntity?> FindEntityAsync(string playerId);
    Task UpdatePlayerAsync(TPlayerEntity player);
    Task DisconnectAsync(string socketId);
    Task DeletePlayerAsync(string playerId);
    Task<TPlayerEntity?> GetPlayerAsync(string playerId);
}


public interface IMovementService<TPlayerEntity, TMovementInput> where TPlayerEntity : PlayerEntity where TMovementInput : MovementInput
{
    Task<TPlayerEntity?> MovePlayerAsync(string playerId, TMovementInput input);
}
