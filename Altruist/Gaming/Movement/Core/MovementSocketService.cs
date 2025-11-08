namespace Altruist.Gaming.Movement;

public interface IMovementSocketService
{
    Task SyncMovement(IMovementPacket movement, string clientId);
}

[Service(typeof(IMovementSocketService))]
public class MovementSocketService : IMovementSocketService
{
    private readonly IPlayerService _playerService;
    private readonly IMovementService _movementService;
    private readonly PlayerCursor<PlayerEntity> _playerCursor;

    private readonly IAltruistRouter _router;

    public MovementSocketService(IPlayerService playerService, IMovementService movementService, IAltruistRouter router, IPlayerCursorFactory playerCursorFactory)
    {
        _playerService = playerService;
        _movementService = movementService;
        _playerCursor = playerCursorFactory.Create<PlayerEntity>();
        _router = router;
    }

    public async virtual Task SyncMovement(IMovementPacket movement, string clientId)
    {
        var player = await _playerService.GetPlayerAsync(clientId);
        if (player == null)
        {
            await _router.Client.SendAsync(
                clientId,
                PacketHelper.Failed($"Cannot move player with id {clientId}. Not found.", clientId, movement.Type));
            return;
        }

        await _movementService.MovePlayerAsync(clientId, movement);
    }

    protected virtual async Task UpdateMovementAsync()
    {
        foreach (var player in _playerCursor)
        {
            await _router.Synchronize.SendAsync(player.Update());
        }
    }
}