AltruistBuilder.Create(args)
    .SetupTransport<WebSocketConnectionSetup>(WebSocketTransportToken.Instance, (setup) =>
    {
        return setup
            .MapPortal<SpaceshipGamePortal>("/game")
            .MapPortal<RegenPortal>("/game")
            .MapRelayPortal<MovementRelayPortal>("localhost", Config.MOVEMENT, "sync-movement");
    })
    .SetupDatabase<ScyllaDBConnectionSetup>(ScyllaDBToken.Instance, (setup) =>
    {
        return setup
            .AddContactPoint("localhost", 9042)
            .CreateKeyspace<DefaultScyllaKeyspace>(setup => setup.AddVault<Player>());
    })
    .UseCache<RedisConnectionSetup>(RedisCacheServiceToken.Instance)
    .EnableEngine(FrameRate.Hz30)
    .StartServer();