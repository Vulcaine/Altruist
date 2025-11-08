namespace Altruist.Gaming;

public interface IGameWorldService
{
    Task<IGameWorldManager?> FindWorldForClientAsync(string clientId);
}

[Service(typeof(IGameWorldService))]
public class GameWorldService : IGameWorldService
{
    private readonly IGameWorldCoordinator _worldCoordinator;
    private readonly IPlayerService _playerService;

    public GameWorldService(IGameWorldCoordinator worldCoordinator, IPlayerService playerService)
    {
        _worldCoordinator = worldCoordinator;
        _playerService = playerService;
    }

    /// <summary>
    /// Finds the game world manager associated with the given client ID.
    /// </summary>
    /// <param name="clientId">The client ID to search for.</param>
    /// <returns>The game world manager associated with the client ID, or null.</returns>
    public async Task<IGameWorldManager?> FindWorldForClientAsync(string clientId)
    {
        var player = await _playerService.GetPlayerAsync(clientId);
        if (player != null)
        {
            var world = _worldCoordinator.GetWorld(player.WorldIndex);
            if (world != null)
            {
                return world;
            }
        }

        return null;
    }

}