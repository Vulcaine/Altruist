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

namespace Altruist.Gaming.Movement;

public abstract class AltruistMovementPortal : Portal
{
    protected readonly IMovementSocketService _movementSocketService;
    protected AltruistMovementPortal(IMovementSocketService movementSocketService)
    {
        _movementSocketService = movementSocketService;
    }

    [Gate("move")]
    public async virtual Task SyncMovement(IMovementPacket movement, string clientId)
    {
        await _movementSocketService.SyncMovement(movement, clientId);
    }

}

// public abstract class AltruistForwardMovementPortal : AltruistMovementPortal
// {
//     public AltruistForwardMovementPortal(MovementPortalContext context, IPlayerService playerService, IMovementService movementService, ILoggerFactory loggerFactory)
//         : base(context, playerService, movementService, loggerFactory) { }

// }

// public abstract class AltruistEightDirectionMovementPortal : AltruistMovementPortal
// {
//     public AltruistEightDirectionMovementPortal(MovementPortalContext context, IPlayerService playerService, IMovementService movementService, ILoggerFactory loggerFactory)
//         : base(context, playerService, movementService, loggerFactory) { }
// }