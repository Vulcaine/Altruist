/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Movement;

public class MovementPortalContext : GamePortalContext
{
    public MovementPortalContext(IAltruistContext altruistContext, IServiceProvider serviceProvider) : base(altruistContext, serviceProvider)
    {
    }
}

public abstract class AltruistMovementPortal<TPlayerEntity, TMovementPacket> : Portal<MovementPortalContext>
    where TPlayerEntity : PlayerEntity, new()
    where TMovementPacket : IMovementPacket
{
    protected readonly IPlayerService<TPlayerEntity> _playerService;
    protected readonly IMovementService<TPlayerEntity> _movementService;
    protected readonly PlayerCursor<TPlayerEntity> _playerCursor;

    protected AltruistMovementPortal(MovementPortalContext context, IPlayerService<TPlayerEntity> playerService, IMovementService<TPlayerEntity> movementService, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
        _playerService = playerService;
        _movementService = movementService;
        _playerCursor = context.PlayerCursorFactory.Create<TPlayerEntity>();
    }

    [Gate("move")]
    public async virtual Task SyncMovement(TMovementPacket movement, string clientId)
    {
        var player = await _playerService.GetPlayerAsync(clientId);
        if (player == null)
        {
            await Router.Client.SendAsync(
                clientId,
                PacketHelper.Failed($"Cannot move player with id {clientId}. Not found.", clientId, movement.Type));
            return;
        }

        await _movementService.MovePlayerAsync(clientId, movement);
    }

    protected void UpdateMovement()
    {
        foreach (var player in _playerCursor)
        {
            _ = Router.Synchronize.SendAsync(player);
        }
    }
}

public abstract class AltruistForwardMovementPortal<TPlayerEntity, TMovementPacket> : AltruistMovementPortal<TPlayerEntity, TMovementPacket> where TPlayerEntity : PlayerEntity, new()
where TMovementPacket : IMovementPacket
{
    public AltruistForwardMovementPortal(MovementPortalContext context, IPlayerService<TPlayerEntity> playerService, IMovementService<TPlayerEntity> movementService, ILoggerFactory loggerFactory)
        : base(context, playerService, movementService, loggerFactory) { }

}

public abstract class AltruistEightDirectionMovementPortal<TPlayerEntity, TMovementPacket> : AltruistMovementPortal<TPlayerEntity, TMovementPacket> where TPlayerEntity : PlayerEntity, new() where TMovementPacket : IMovementPacket
{
    public AltruistEightDirectionMovementPortal(MovementPortalContext context, IPlayerService<TPlayerEntity> playerService, IMovementService<TPlayerEntity> movementService, ILoggerFactory loggerFactory)
        : base(context, playerService, movementService, loggerFactory) { }
}