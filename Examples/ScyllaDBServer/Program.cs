﻿/* 
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
using Altruist.Security;
using Altruist.Redis;
using Altruist.ScyllaDB;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args)
    .SetupGameEngine(setup => setup
        .AddWorld(new MainWorldIndex(0, new Vector2(100, 100)))
        .EnableEngine(FrameRate.Hz30))
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game").MapPortal<RegenPortal>("/game").MapPortal<MyAuthPortal>("/game"))
    .WithRedis(setup => setup.ForgeDocuments())
    .WithScyllaDB(setup => setup.CreateKeyspace<DefaultScyllaKeyspace>(
        setup => setup.ForgeVaults()
    ))
    .WebApp(setup => setup.AddJwtAuth().StatefulToken<DefaultScyllaKeyspace>())
    .StartServer();