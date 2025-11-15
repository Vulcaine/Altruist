/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

namespace Altruist.Gaming;

public abstract class AltruistGameSessionPortal : Portal
{
    protected readonly IGameSessionService _gameSessionService;
    protected readonly IAltruistRouter _router;

    protected AltruistGameSessionPortal(
        IGameSessionService gameSessionService,
        IAltruistRouter router)
    {
        _gameSessionService = gameSessionService;
        _router = router;
    }

    // ---------------------------
    // Public API: fixed template
    // ---------------------------

    [Gate(IngressEP.Handshake)]
    public async Task HandshakeAsync(
        HandshakePacket message,
        string clientId)
    {
        // 1) Core logic from service
        var serviceResult = await _gameSessionService.HandshakeAsync(message, clientId);

        // 2) Let user adjust/extend the result
        var finalResult = await OnHandshakeReceived(message, clientId, serviceResult);

        // 3) Publish
        await PublishResultAsync(clientId, finalResult);
    }

    [Gate(IngressEP.LeaveGame)]
    public async Task ExitGameAsync(
        LeaveGamePacket message,
        string clientId)
    {
        // Service returns optional room broadcast
        var serviceResult = await _gameSessionService.ExitGameAsync(message, clientId);

        var finalResult = await OnExitGameReceived(message, clientId, serviceResult);

        await PublishResultAsync(finalResult);
    }

    [Gate(IngressEP.JoinGame)]
    public async Task JoinGameAsync(
        JoinGamePacket message,
        string clientId)
    {
        var serviceResult = await _gameSessionService.JoinGameAsync(message, clientId);

        var finalResult = await OnJoinGameReceived(message, clientId, serviceResult);

        await PublishResultAsync(clientId, finalResult);
    }

    public override Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        _gameSessionService.ClearAllContexts(clientId);
        return Task.CompletedTask;
    }

    public async Task Cleanup()
    {
        await _gameSessionService.Cleanup();
    }

    // -----------------------------------
    // Hooks: default returns input result
    // -----------------------------------

    protected virtual Task<IResultPacket> OnHandshakeReceived(
        HandshakePacket message,
        string clientId,
        IResultPacket result)
        => Task.FromResult(result);

    protected virtual Task<RoomBroadcast?> OnExitGameReceived(
        LeaveGamePacket message,
        string clientId,
        RoomBroadcast? result)
        => Task.FromResult(result);

    protected virtual Task<IResultPacket> OnJoinGameReceived(
        JoinGamePacket message,
        string clientId,
        IResultPacket result)
        => Task.FromResult(result);

    // -----------------------------------
    // Publishing logic (framework owned)
    // -----------------------------------

    // For handshake / join (ResultPacket → client)
    protected virtual async Task PublishResultAsync(string clientId, IResultPacket result)
    {
        if (result is IResultPacketWithPayload payloadPacket)
        {
            var packet = payloadPacket.Payload;
            var receiver = packet.Header.Receiver;

            if (string.IsNullOrEmpty(receiver))
            {
                receiver = clientId;
                packet.Header.SetReceiver(receiver);
            }

            await _router.Client.SendAsync(receiver, packet);
        }

    }

    // For exit game (RoomBroadcast → room)
    protected virtual async Task PublishResultAsync(RoomBroadcast? result)
    {
        if (result is null)
            return;

        await _router.Room.SendAsync(result.RoomId, result.Packet);
    }
}
