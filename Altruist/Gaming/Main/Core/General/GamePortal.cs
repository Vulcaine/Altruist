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

namespace Altruist.Gaming;

public abstract class AltruistGameSessionPortal : IPortal
{
    protected readonly IGameSessionService _gameSessionService;
    protected AltruistGameSessionPortal(IGameSessionService gameSessionService)
    {
        _gameSessionService = gameSessionService;
    }

    [Gate(IngressEP.Handshake)]
    public async virtual Task HandshakeAsync(HandshakePacket message, string clientId)
    {
        await _gameSessionService.HandshakeAsync(message, clientId);
    }

    [Gate(IngressEP.LeaveGame)]
    public async virtual Task ExitGameAsync(LeaveGamePacket message, string clientId)
    {
        await _gameSessionService.ExitGameAsync(message, clientId);
    }

    [Gate(IngressEP.JoinGame)]
    public async virtual Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        await _gameSessionService.JoinGameAsync(message, clientId);
    }

    public async Task Cleanup()
    {
        await _gameSessionService.Cleanup();
    }
}
