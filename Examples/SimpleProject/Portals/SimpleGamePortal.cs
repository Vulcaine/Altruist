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

using Altruist.Gaming;
using Microsoft.Extensions.Logging;
using SimpleGame.Entities;

namespace Portals;

public class SimpleGamePortal : AltruistGameSessionPortal<SimpleSpaceship>
{
    public SimpleGamePortal(GamePortalContext context, GameWorldCoordinator gameWorld, IPlayerService<SimpleSpaceship> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, playerService, loggerFactory)
    {
    }
}