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

namespace Altruist.Gaming;

public abstract class AltruistMovementPortal<TPlayerEntity, TMovementInput> : Portal
    where TPlayerEntity : PlayerEntity, new()
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

public abstract class AltruistForwardMovementPortal<TMovementInput> : AltruistMovementPortal<TMovementInput, ForwardMovementInput> where TMovementInput : PlayerEntity, new()
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

public abstract class AltruistEightDirectionMovementPortal<TMovementInput> : AltruistMovementPortal<TMovementInput, EightDirectionMovementInput> where TMovementInput : PlayerEntity, new()
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