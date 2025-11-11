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
using Altruist.Gaming.ThreeD;
using Altruist.UORM;

namespace SimpleGame.Entities;

[Vault("player")]
public class Spaceship : VaultModel
{
}

[Prefab("Spaceship")]
public class SimpleSpaceshipPrefab : Prefab3D
{
    [PostConstruct]
    public void Init()
    {
        // Build the prefab structure with the editor (no DB writes here)
        var editor = new PrefabEditor3D(this);
        editor.Add(new Spaceship());
        // editor.Pop(); // optional if you plan to add siblings after
    }
}
