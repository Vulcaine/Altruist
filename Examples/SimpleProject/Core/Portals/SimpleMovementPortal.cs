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

using Altruist.Gaming.Movement.ThreeD;

namespace Altruist.SimpleGame.Portals;

[Portal("/game")]
public class SimpleMovementPortal : IPortal
{
    private readonly IMovementManager3D _movementManager;
    public SimpleMovementPortal(IMovementManager3D movementManager)
    {
        _movementManager = movementManager;
    }

    [Gate("move")]
    public void Move3D(MoveRequest3D req, string clientId)
    {
        var intent = MovementIntent3D.FromButtons(
            forward: req.Forward == true,
            back: req.Back == true,
            left: req.Left == true,
            right: req.Right == true,
            flyUp: req.FlyUp == true,
            flyDown: req.FlyDown == true,
            turnYaw: req.TurnYaw ?? 0f,
            jump: req.Jump == true,
            boost: req.Boost == true,
            dash: req.Dash == true
        );

        _movementManager.SetPlayerIntent(clientId, intent);
    }
}