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

public abstract class AltruistMovementPortal<TPlayerEntity, TMovementPacket> : Portal
    where TPlayerEntity : PlayerEntity, new()
    where TMovementPacket : IMovementPacket
{
    private readonly IPlayerService<TPlayerEntity> _playerService;
    private readonly IMovementService<TPlayerEntity> _movementService;

    protected AltruistMovementPortal(IPortalContext context, IPlayerService<TPlayerEntity> playerService, IMovementService<TPlayerEntity> movementService, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
        _playerService = playerService;
        _movementService = movementService;
    }

    [Gate("sync-movement")]
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

        var updatedEntity = await _movementService.MovePlayerAsync(clientId, movement);

        if (updatedEntity != null)
        {
            await Router.Synchronize.SendAsync(updatedEntity);
        }
    }
}

public abstract class AltruistForwardMovementPortal<TPlayerEntity, TMovementPacket> : AltruistMovementPortal<TPlayerEntity, TMovementPacket> where TPlayerEntity : PlayerEntity, new()
where TMovementPacket : IMovementPacket
{
    public AltruistForwardMovementPortal(IPortalContext context, IPlayerService<TPlayerEntity> playerService, IMovementService<TPlayerEntity> movementService, ILoggerFactory loggerFactory)
        : base(context, playerService, movementService, loggerFactory) { }

}

public abstract class AltruistEightDirectionMovementPortal<TPlayerEntity, TMovementPacket> : AltruistMovementPortal<TPlayerEntity, TMovementPacket> where TPlayerEntity : PlayerEntity, new() where TMovementPacket : IMovementPacket
{
    public AltruistEightDirectionMovementPortal(IPortalContext context, IPlayerService<TPlayerEntity> playerService, IMovementService<TPlayerEntity> movementService, ILoggerFactory loggerFactory)
        : base(context, playerService, movementService, loggerFactory) { }
}