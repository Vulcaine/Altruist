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
using Altruist.Database;
using Altruist.Gaming;
using Altruist.UORM;
using Microsoft.Extensions.Logging;

namespace Portals;

public class SimpleGamePortal : AltruistGameSessionPortal<SpaceshipPlayer>
{
    public SimpleGamePortal(IPortalContext context, GameWorldCoordinator gameWorld, IPlayerService<SpaceshipPlayer> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, playerService, loggerFactory)
    {
    }
}


[Vault("player")]
[VaultPrimaryKey(keys: [nameof(GenId), nameof(Name)])]
public class SpaceshipPlayer : Spaceship, IOnVaultCreate
{
    public Task<List<IVaultModel>> OnCreateAsync(IServiceProvider serviceProvider)
    {
        var aPlayer = new SpaceshipPlayer() { GenId = "Test", Name = "MyPlayerName" };
        return Task.FromResult(new List<IVaultModel> { aPlayer });
    }
}