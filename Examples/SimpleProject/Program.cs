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

using System.Numerics;
using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.Engine;
using Altruist.Web;

using Portals;

AltruistBuilder.Create(args)
    .SetupGameEngine(setup => setup
        .AddWorld(new MainWorldIndex(0, new Vector2(100, 100), new Vector2(0, 0)))
        .EnableEngine(FrameRate.Hz30, CycleUnit.Seconds))
    .WithWebsocket(setup =>
    setup.MapPortal<SimpleGamePortal>("/game").MapPortal<SimpleMovementPortal>("/game"))
    .WebApp()
    .StartServer();

public class MainWorldIndex : WorldIndex
{
    public MainWorldIndex(int index, Vector2 size, Vector2? gravity = null) : base(index, size, gravity)
    {
    }
}
