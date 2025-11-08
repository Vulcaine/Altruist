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

using Altruist.Physx;
using Box2DSharp.Dynamics;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Movement;

public abstract class BaseMovementService : IMovementService
{
    protected readonly ILogger _logger;
    protected readonly ICacheProvider _cacheProvider;
    protected readonly MovementPhysx _movementPhysx;
    protected readonly IPlayerService _playerService;

    public BaseMovementService(
    IPlayerService playerService,
        MovementPhysx movementPhysx,
        ICacheProvider cacheProvider,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<BaseMovementService>();
        _playerService = playerService;
        _cacheProvider = cacheProvider;
        _movementPhysx = movementPhysx;
    }

    public async Task<PlayerEntity?> MovePlayerAsync(string playerId, IMovementPacket input)
    {
        try
        {
            PlayerEntity? entity = await _playerService.FindEntityAsync(playerId);
            if (entity == null) return null;
            if (entity.PhysxBody == null) throw new Exception("Illegal state of player entity. Physx body is null.");

            var body = entity.PhysxBody;
            // ApplyRotation(body, entity, input);
            ApplyMovement(body, entity, input);
            // UpdateEntityPosition(entity, body);
            await _cacheProvider.SaveAsync(entity.SysId, entity);
            return entity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error while moving player {ex.Message}");
        }

        return null;
    }
    // protected abstract void ApplyRotation(Body body, TPlayerEntity entity, IMovementPacket input);
    protected abstract void ApplyMovement(Body body, PlayerEntity entity, IMovementPacket input);

    protected void ClampSpeed(PlayerEntity entity)
    {
        entity.CurrentSpeed = Math.Clamp(entity.CurrentSpeed, 0, entity.MaxSpeed);
    }


    protected void UpdateEntityPosition(PlayerEntity entity, Body body)
    {
        entity.Position = [body.GetPosition().X, body.GetPosition().Y];
        entity.Rotation = body.GetTransform().Rotation.Angle;
    }
}

[Service]
public class ForwardMovementService : BaseMovementService
{
    public ForwardMovementService(
        IPlayerService playerService,
        MovementPhysx movementPhysx,
        ICacheProvider cacheProvider,
        ILoggerFactory loggerFactory)
        : base(playerService, movementPhysx, cacheProvider, loggerFactory) { }

    protected override void ApplyMovement(Body body, PlayerEntity entity, IMovementPacket input)
    {
        if (input is not ForwardMovementPacket forwardMovementPacket) return;

        var movementInput = new ForwardMovementPhysxInput
        {
            Acceleration = entity.Acceleration,
            Deceleration = entity.Deceleration,
            MaxSpeed = entity.MaxSpeed,
            CurrentSpeed = entity.CurrentSpeed,
            RotationSpeed = entity.RotationSpeed,
            RotateLeft = forwardMovementPacket.RotateLeft,
            RotateRight = forwardMovementPacket.RotateRight,
            Turbo = forwardMovementPacket.Turbo,
            MoveForward = forwardMovementPacket.MoveUp
        };

        var result = _movementPhysx.Forward.CalculateMovement(body, movementInput);
        entity.Moving = result.Moving;

        if (result.Moving)
        {
            entity.CurrentSpeed = result.CurrentSpeed;
            ClampSpeed(entity);
            _movementPhysx.ApplyMovement(body, result);
        }
        else
        {
            entity.Moving = false;
            var deceleration = _movementPhysx.Forward.CalculateDeceleration(body, movementInput);
            _movementPhysx.ApplyMovement(body, deceleration);
        }
    }
}

[Service]
public class EightDirectionMovementService : BaseMovementService
{
    public EightDirectionMovementService(IPlayerService playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
        : base(playerService, physx, cacheProvider, loggerFactory) { }

    protected override void ApplyMovement(Body body, PlayerEntity entity, IMovementPacket input)
    {
        if (input is not EightDirectionMovementPacket eightDirectionIMovementPacket) return;

        var movementInput = new EightDirectionMovementPhysxInput
        {
            Acceleration = entity.Acceleration,
            Deceleration = entity.Deceleration,
            MaxSpeed = entity.MaxSpeed,
            CurrentSpeed = entity.CurrentSpeed,
            MoveUp = eightDirectionIMovementPacket.MoveUp,
            MoveDown = eightDirectionIMovementPacket.MoveDown,
            MoveLeft = eightDirectionIMovementPacket.MoveLeft,
            MoveRight = eightDirectionIMovementPacket.MoveRight,
            Turbo = eightDirectionIMovementPacket.Turbo
        };

        var result = _movementPhysx.EightDirection.CalculateMovement(body, movementInput);
        entity.Moving = result.Moving;

        if (result.Moving)
        {
            entity.CurrentSpeed = result.CurrentSpeed;
            ClampSpeed(entity);
            _movementPhysx.ApplyMovement(body, result);
        }
        else
        {
            var deceleration = _movementPhysx.EightDirection.CalculateDeceleration(body, movementInput);
            _movementPhysx.ApplyMovement(body, deceleration);
        }
    }
}

[Service]
public class EightDirectionVehicleMovementService : EightDirectionMovementService
{
    public EightDirectionVehicleMovementService(IPlayerService playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
        : base(playerService, physx, cacheProvider, loggerFactory) { }

    protected override void ApplyMovement(Body body, PlayerEntity vehicle, IMovementPacket input)
    {
        if (input is not EightDirectionMovementPacket eightDirectionMovementPacket || vehicle is not Vehicle v) return;
        base.ApplyMovement(body, vehicle, input);
        HandleTurboFuel(v, eightDirectionMovementPacket.Turbo);
    }

    private void HandleTurboFuel(Vehicle vehicle, bool turbo)
    {
        if (turbo && vehicle.TurboFuel > 0)
        {
            vehicle.TurboFuel -= 1;
        }
        else
        {
            vehicle.TurboFuel = Math.Min(vehicle.TurboFuel + 0.05f, vehicle.MaxTurboFuel);
        }
    }
}

[Service]
public class ForwardSpacehipMovementService : ForwardMovementService
{
    protected ForwardSpacehipMovementService(IPlayerService playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory) : base(playerService, physx, cacheProvider, loggerFactory)
    {
    }

    protected override void ApplyMovement(Body body, PlayerEntity vehicle, IMovementPacket input)
    {
        if (input is not ForwardMovementPacket forwardMovementPacket || vehicle is not Spaceship spaceship) return;
        base.ApplyMovement(body, vehicle, input);
        HandleTurboFuel(spaceship, forwardMovementPacket.Turbo);
    }

    private void HandleTurboFuel(Spaceship vehicle, bool turbo)
    {
        if (turbo && vehicle.TurboFuel > 0)
        {
            vehicle.TurboFuel -= 1;
        }
        else
        {
            vehicle.TurboFuel = Math.Min(vehicle.TurboFuel + 0.05f, vehicle.MaxTurboFuel);
        }
    }
}
