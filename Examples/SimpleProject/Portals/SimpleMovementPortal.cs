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

using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.Movement;
using Microsoft.Extensions.Logging;
using SimpleGame.Entities;

namespace Portals;

public class SimpleMovementPortal : AltruistForwardMovementPortal<SimpleSpaceship, ForwardMovementPacket>
{
    public SimpleMovementPortal(MovementPortalContext context, IPlayerService<SimpleSpaceship> playerService, IMovementService<SimpleSpaceship> movementService, ILoggerFactory loggerFactory) : base(context, playerService, movementService, loggerFactory)
    {
    }

    [Cycle]
    protected override async Task UpdateMovementAsync()
    {
        await base.UpdateMovementAsync();
    }
}