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

namespace Altruist;

/// <summary>Built-in server-to-client event names used by the framework's session/room flow. Define your own constants for application events.</summary>
public static class OutgressEP
{
    /// <summary>Sent when a player joined a room.</summary>
    public const string NotifyPlayerJoinedRoom = "room-joined";
    /// <summary>Sent when a player left a room.</summary>
    public const string NotifyPlayerLeftRoom = "player-left";
    /// <summary>Sent when a game session started.</summary>
    public const string NotifyGameStarted = "game-started";
    /// <summary>Sent when a requested operation failed.</summary>
    public const string NotifyFailed = "failed";
    /// <summary>Entity sync (delta) packet.</summary>
    public const string NotifySync = "sync";
}

/// <summary>Built-in client-to-server event names handled by the framework's game session portal (use as <c>[Gate(IngressEP.JoinGame)]</c>).</summary>
public static class IngressEP
{
    /// <summary>Initial handshake after connecting.</summary>
    public const string Handshake = "handshake";
    /// <summary>Request to join a game/room.</summary>
    public const string JoinGame = "join-game";
    /// <summary>Forward a message to other clients.</summary>
    public const string Forward = "forward-message";
    /// <summary>Request to leave the current game/room.</summary>
    public const string LeaveGame = "leave-game";
    /// <summary>Example/demo shoot action.</summary>
    public const string Shoot = "SHOOT";
}
