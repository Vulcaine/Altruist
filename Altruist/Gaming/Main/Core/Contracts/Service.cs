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

public interface IPlayerService<TPlayerEntity> : ICleanUp where TPlayerEntity : PlayerEntity, new()
{
    Task<TPlayerEntity?> ConnectById(string roomId, string socketId, string name, int worldIndex, float[]? positon = null);
    Task<TPlayerEntity?> FindEntityAsync(string playerId);
    Task UpdatePlayerAsync(TPlayerEntity player);
    Task DisconnectAsync(string socketId);
    Task DeletePlayerAsync(string playerId);
    Task<TPlayerEntity?> GetPlayerAsync(string playerId);
}

public interface IPlayerCursorFactory
{
    PlayerCursor<T> Create<T>() where T : notnull, PlayerEntity;
}
