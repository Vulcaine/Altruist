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
using Altruist.Gaming.Movement.ThreeD;
using SimpleGame;

namespace Portals;

[Portal("/game")]
public class SimpleMovementPortal : IPortal
{
    private readonly IMovementManager3D _movementManager;
    public SimpleMovementPortal(IMovementManager3D movementManager)
    {
        _movementManager = movementManager;
    }

    [Gate("move")]
    public void MoveRequest(string clientId, MoveRequest3D moveRequest)
    {
        // Build world move vector from flags
        var move = moveRequest.GetDirection();
        if (move.LengthSquared() > 1f) move = Vector3.Normalize(move);

        // Signed yaw turn (-1..+1)
        var yaw = moveRequest.GetTurn();

        var intent = new MovementIntent3D(
            Move: move,
            TurnYaw: yaw,
            Jump: moveRequest.WantsJump(),
            AimDirection: default,
            Boost: false,
            Dash: false,
            Knockback: default
        );

        _movementManager.SetPlayerIntent(clientId, intent);
    }
}