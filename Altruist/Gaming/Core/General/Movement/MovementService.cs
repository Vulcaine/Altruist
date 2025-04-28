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
using FarseerPhysics.Factories;
using Microsoft.Extensions.Logging;
using Microsoft.Xna.Framework;

namespace Altruist.Gaming;

public abstract class BaseMovementService<TPlayerEntity> : IMovementService<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    protected readonly ILogger _logger;
    protected readonly ICacheProvider _cacheProvider;
    protected readonly MovementPhysx _movementPhysx;
    protected readonly IPlayerService<TPlayerEntity> _playerService;

    public BaseMovementService(
        IPortalContext portalContext,
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

            var body = CreatePhysxBody(entity);
            ApplyRotation(body, entity, input);
            ApplyMovement(body, entity, input);
            UpdateEntityPosition(entity, body);
            await _cacheProvider.SaveAsync(entity.SysId, entity);
            return entity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error while moving player {ex.Message}");
        }

        return null;
    }
    protected abstract void ApplyRotation(Body body, TPlayerEntity entity, IMovementPacket input);
    protected abstract void ApplyMovement(Body body, TPlayerEntity entity, IMovementPacket input);

    protected void ClampSpeed(TPlayerEntity entity)
    {
        entity.CurrentSpeed = Math.Clamp(entity.CurrentSpeed, 0, entity.MaxSpeed);
    }

    protected virtual Body CreatePhysxBody(TPlayerEntity entity)
    {
        var body = BodyFactory.CreateRectangle(new World(new Vector2(0, 0)), 40f, 20f, 1f, new Vector2(entity.Position[0], entity.Position[1]));
        body.Rotation = entity.Rotation;
        return body;
    }

    protected void UpdateEntityPosition(TPlayerEntity entity, Body body)
    {
        entity.Position = [body.Position.X, body.Position.Y];
        entity.Rotation = body.Rotation;
    }
}

public abstract class ForwardMovementService<T> : BaseMovementService<T> where T : PlayerEntity, new()
{
    public ForwardMovementService(IPortalContext context,
        IPlayerService<T> playerService,
        MovementPhysx movementPhysx,
        ICacheProvider cacheProvider,
        ILoggerFactory loggerFactory)
        : base(context, playerService, movementPhysx, cacheProvider, loggerFactory) { }

    protected override void ApplyRotation(Body body, T entity, IMovementPacket input)
    {
        if (input is ForwardMovementPacket forwardMovementPacket && (forwardMovementPacket.RotateRight || forwardMovementPacket.RotateLeft))
        {
            body.Rotation += forwardMovementPacket.RotateRight ? entity.RotationSpeed : -entity.RotationSpeed;
        }
    }

    protected override void ApplyMovement(Body body, T entity, IMovementPacket input)
    {
        if (input is not ForwardMovementPacket forwardMovementPacket) return;

        var movementInput = new ForwardMovementPhysxInput
        {
            Acceleration = entity.Acceleration,
            Deceleration = entity.Deceleration,
            MaxSpeed = entity.MaxSpeed,
            CurrentSpeed = entity.CurrentSpeed,
            RotateLeft = forwardMovementPacket.RotateLeft,
            RotateRight = forwardMovementPacket.RotateRight,
            Turbo = forwardMovementPacket.Turbo
        };

        var result = _movementPhysx.Forward.ApplyMovement(body, movementInput);
        entity.Moving = result.Moving;

        if (result.Moving)
        {
            entity.CurrentSpeed = result.CurrentSpeed;
            entity.CurrentSpeed = result.CurrentSpeed;
            ClampSpeed(entity);
        }
        else
        {
            entity.Moving = false;
            _movementPhysx.Forward.ApplyDeceleration(body, movementInput);
        }
    }
}

public abstract class EightDirectionMovementService<T> : BaseMovementService<T> where T : PlayerEntity, new()
{
    public EightDirectionMovementService(IPortalContext context, IPlayerService<T> playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
        : base(context, playerService, physx, cacheProvider, loggerFactory) { }

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

        var result = _movementPhysx.EightDirection.ApplyMovement(body, movementInput);
        entity.Moving = result.Moving;

        if (result.Moving)
        {
            entity.CurrentSpeed = result.CurrentSpeed;
            ClampSpeed(entity);
        }
        else
        {
            _movementPhysx.EightDirection.ApplyDeceleration(body, movementInput);
        }
    }
}

public abstract class EightDirectionVehicleMovementService<TPlayerEntity> : EightDirectionMovementService<TPlayerEntity> where TPlayerEntity : Vehicle, new()
{
    public EightDirectionVehicleMovementService(IPortalContext context, IPlayerService<TPlayerEntity> playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
        : base(context, playerService, physx, cacheProvider, loggerFactory) { }


    protected override void ApplyRotation(Body body, TPlayerEntity entity, IMovementPacket input)
    {
        body.Rotation += entity.RotationSpeed;
    }


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
    protected ForwardSpacehipMovementService(IPortalContext context, IPlayerService<TPlayerEntity> playerService, MovementPhysx physx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory) : base(context, playerService, physx, cacheProvider, loggerFactory)
    {
    }

    protected override void ApplyRotation(Body body, TPlayerEntity vehicle, IMovementPacket input)
    {
        base.ApplyRotation(body, vehicle, input);
    }

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
