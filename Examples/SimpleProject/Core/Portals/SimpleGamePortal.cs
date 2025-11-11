/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using SimpleGame.Entities;

namespace Portals;

[Portal("/game")]
public class SimpleGamePortal : AltruistGameSessionPortal
{
    private readonly IGameWorldManager3D _gameworldManager;
    private readonly IPrefabManager3D _prefabManager;

    public SimpleGamePortal(
        IGameSessionService gameSessionService,
        IGameWorldManager3D gameworldManager,
        IPrefabManager3D prefabManager)
        : base(gameSessionService)
    {
        _gameworldManager = gameworldManager;
        _prefabManager = prefabManager;
    }

    [Gate(IngressEP.JoinGame)]
    public override async Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        await base.JoinGameAsync(message, clientId);

        // THIS SHOULD GO ONCE AT STARTUP OR CHARACTER CREATION
        // await _prefabManager.CreateAsync<SimpleSpaceshipPrefab>(cfg =>
        // {
        //     cfg.Get<Spaceship>().Speed = 5;       // optional
        // });

        var handle = await _prefabManager.LoadAsync<SimpleSpaceshipPrefab>("");
        var shipInstance = handle.Manifest;
        await _gameworldManager.AddDynamicObject(shipInstance);
    }
}
