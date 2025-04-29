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
using FarseerPhysics.Dynamics;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Movement;

public abstract class BaseMovementService<TPlayerEntity> : IMovementService<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    protected readonly ILogger _logger;
    protected readonly ICacheProvider _cacheProvider;
    protected readonly MovementPhysx _movementPhysx;
    protected readonly IPlayerService<TPlayerEntity> _playerService;

    public BaseMovementService(
    IPlayerService<TPlayerEntity> playerService,
        MovementPhysx movementPhysx,
        ICacheProvider cacheProvider,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<BaseMovementService<TPlayerEntity>>();
        _playerService = playerService;
        _cacheProvider = cacheProvider;
        _movementPhysx = movementPhysx;
    }

    public async Task<TPlayerEntity?> MovePlayerAsync(string playerId, IMovementPacket input)
    {
        try
        {
            TPlayerEntity? entity = await _playerService.FindEntityAsync(playerId);
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
    protected abstract void ApplyMovement(Body body, TPlayerEntity entity, IMovementPacket input);

    protected void ClampSpeed(TPlayerEntity entity)
    {
        entity.CurrentSpeed = Math.Clamp(entity.CurrentSpeed, 0, entity.MaxSpeed);
    }


    protected void UpdateEntityPosition(TPlayerEntity entity, Body body)
    {
        entity.Position = [body.Position.X, body.Position.Y];
        entity.Rotation = body.Rotation;
    }
}

public abstract class ForwardMovementService<T> : BaseMovementService<T> where T : PlayerEntity, new()
{
    public ForwardMovementService(
        IPlayerService<T> playerService,
        MovementPhysx movementPhysx,
        ICacheProvider cacheProvider,
        ILoggerFactory loggerFactory)
        : base(playerService, movementPhysx, cacheProvider, loggerFactory) { }

    protected override void ApplyMovement(Body body, T entity, IMovementPacket input)
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

public abstract class EightDirectionMovementService<T> : BaseMovementService<T> where T : PlayerEntity, new()
{
    public EightDirectionMovementService(IPlayerService<T> playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
        : base(playerService, physx, cacheProvider, loggerFactory) { }

    protected override void ApplyMovement(Body body, T entity, IMovementPacket input)
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

public abstract class EightDirectionVehicleMovementService<TPlayerEntity> : EightDirectionMovementService<TPlayerEntity> where TPlayerEntity : Vehicle, new()
{
    public EightDirectionVehicleMovementService(IPlayerService<TPlayerEntity> playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
        : base(playerService, physx, cacheProvider, loggerFactory) { }


    // protected override void ApplyRotation(Body body, TPlayerEntity entity, IMovementPacket input)
    // {
    //     body.Rotation += entity.RotationSpeed;
    // }


    protected override void ApplyMovement(Body body, TPlayerEntity vehicle, IMovementPacket input)
    {
        if (input is not EightDirectionMovementPacket eightDirectionMovementPacket) return;
        base.ApplyMovement(body, vehicle, input);
        HandleTurboFuel(vehicle, eightDirectionMovementPacket.Turbo);
    }

    private void HandleTurboFuel(TPlayerEntity vehicle, bool turbo)
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

public abstract class ForwardSpacehipMovementService<TPlayerEntity> : ForwardMovementService<TPlayerEntity> where TPlayerEntity : Spaceship, new()
{
    protected ForwardSpacehipMovementService(IPlayerService<TPlayerEntity> playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory) : base(playerService, physx, cacheProvider, loggerFactory)
    {
    }

    // protected override void ApplyRotation(Body body, TPlayerEntity vehicle, IMovementPacket input)
    // {
    //     base.ApplyRotation(body, vehicle, input);
    // }

    protected override void ApplyMovement(Body body, TPlayerEntity vehicle, IMovementPacket input)
    {
        if (input is not ForwardMovementPacket forwardMovementPacket) return;
        base.ApplyMovement(body, vehicle, input);
        HandleTurboFuel(vehicle, forwardMovementPacket.Turbo);
    }

    private void HandleTurboFuel(TPlayerEntity vehicle, bool turbo)
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
