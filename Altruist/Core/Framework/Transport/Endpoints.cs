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

public static class OutgressEP
{
    public const string NotifyPlayerJoinedRoom = "room-joined";
    public const string NotifyPlayerLeftRoom = "player-left";
    public const string NotifyGameStarted = "game-started";
    public const string NotifyFailed = "failed";
    public const string NotifySync = "sync";
}

public static class IngressEP
{
    public const string Handshake = "handshake";
    public const string JoinGame = "join-game";
    public const string LeaveGame = "leave-game";
    public const string Shoot = "SHOOT";
}
