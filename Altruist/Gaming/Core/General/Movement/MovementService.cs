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

using FarseerPhysics.Dynamics;
using FarseerPhysics.Factories;
using Microsoft.Extensions.Logging;
using Microsoft.Xna.Framework;
using StackExchange.Redis;

namespace Altruist.Gaming;

public abstract class BaseMovementService<TPlayerEntity, MI> where TPlayerEntity : PlayerEntity, new() where MI : MovementInput
{
    protected readonly ILogger<BaseMovementService<TPlayerEntity, MI>> _logger;
    protected readonly IPlayerService<TPlayerEntity> _playerService;

    public BaseMovementService(
        IPlayerService<TPlayerEntity> playerService,
        ILogger<BaseMovementService<TPlayerEntity, MI>> logger, PortalContext portalContext)
    {
        _logger = logger;
        _playerService = playerService;
    }

    public async Task<TPlayerEntity?> MovePlayerAsync(string playerId, MI input)
    {
        try
        {
            TPlayerEntity? entity = await _playerService.FindEntityAsync(playerId);
            if (entity == null) return null;

            var body = CreatePhysxBody(entity);
            ApplyRotation(body, input);
            ApplyMovement(body, entity, input);

            ClampSpeed(entity);
            UpdateEntityPosition(entity, body);
            await StoreEntityInRedis(playerId, entity);

            return entity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while moving player");
        }

        return null;
    }
    protected abstract void ApplyRotation(Body body, MI input);

    protected abstract void ApplyMovement(Body body, TPlayerEntity entity, MI input);

    protected virtual void ApplyDeceleration(Body body, TPlayerEntity entity)
    {
        Vector2 direction = new Vector2((float)Math.Cos(body.Rotation), (float)Math.Sin(body.Rotation));
        float decelerationForce = -entity.Deceleration * entity.CurrentSpeed;
        var force = direction * decelerationForce;

        body.ApplyForce(ref force);
        entity.CurrentSpeed = Math.Max(0, entity.CurrentSpeed - entity.Deceleration);
    }

    protected void ClampSpeed(TPlayerEntity entity)
    {
        entity.CurrentSpeed = Math.Clamp(entity.CurrentSpeed, 0, entity.MaxSpeed);
    }

    protected virtual Body CreatePhysxBody(TPlayerEntity entity)
    {
        return BodyFactory.CreateRectangle(null, 40f, 20f, 1f, new Vector2(entity.Position[0], entity.Position[1]), entity.Rotation);
    }

    protected void UpdateEntityPosition(TPlayerEntity entity, Body body)
    {
        entity.Position = [body.Position.X, body.Position.Y];
        entity.Rotation = body.Rotation;
    }

    protected async Task StoreEntityInRedis(string playerId, TPlayerEntity entity)
    {
        var database = ConnectionMultiplexer.Connect("localhost").GetDatabase();
        await database.HashSetAsync(playerId,
        [
            new HashEntry("entity.position.x", entity.Position[0].ToString()),
            new HashEntry("entity.position.y", entity.Position[1].ToString()),
            new HashEntry("entity.rotation", entity.Rotation.ToString()),
            new HashEntry("entity.currentSpeed", entity.CurrentSpeed.ToString())
        ]);
    }
}

public abstract class ForwardMovementService<T, MI> : BaseMovementService<T, MI> where T : PlayerEntity, new() where MI : ForwardMovementInput
{
    public ForwardMovementService(
        IPlayerService<T> playerService,
        ILogger<BaseMovementService<T, MI>> logger, PortalContext hubContext)
        : base(playerService, logger, hubContext) { }

    protected override void ApplyMovement(Body body, T entity, MI input)
    {
        if (input.MoveUp)
        {
            Vector2 direction = new Vector2((float)Math.Cos(body.Rotation), (float)Math.Sin(body.Rotation));
            float accelerationFactor = input.Turbo ? 2.0f : 1.0f;

            entity.CurrentSpeed += entity.Acceleration * accelerationFactor;
            entity.CurrentSpeed = Math.Min(entity.CurrentSpeed, entity.MaxSpeed);

            Vector2 velocity = direction * entity.CurrentSpeed;
            body.LinearVelocity = velocity;
        }
        else
        {
            ApplyDeceleration(body, entity);
        }
    }
}

public abstract class EightDirectionMovementService<T, MI> : BaseMovementService<T, MI> where T : PlayerEntity, new() where MI : EightDirectionMovementInput
{
    public EightDirectionMovementService(IPlayerService<T> playerService, ILogger<BaseMovementService<T, MI>> logger, PortalContext hubContext)
        : base(playerService, logger, hubContext) { }

    protected override void ApplyMovement(Body body, T entity, MI input)
    {
        Vector2 direction = Vector2.Zero;

        if (input.MoveUp) direction += new Vector2(0, -1);
        if (input.MoveDown) direction += new Vector2(0, 1);
        if (input.MoveLeft) direction += new Vector2(-1, 0);
        if (input.MoveRight) direction += new Vector2(1, 0);

        if (direction == Vector2.Zero)
        {
            ApplyDeceleration(body, entity);
        }
        else
        {
            if (direction.LengthSquared() > 0) direction.Normalize();

            float accelerationFactor = input.Turbo ? 2.0f : 1.0f;
            entity.CurrentSpeed += entity.Acceleration * accelerationFactor;
            entity.CurrentSpeed = Math.Min(entity.CurrentSpeed, entity.MaxSpeed);

            Vector2 velocity = direction * entity.CurrentSpeed;
            body.LinearVelocity = velocity;
        }
    }
}


public abstract class EightDirectionVehicleMovementService<TPlayerEntity> : EightDirectionMovementService<TPlayerEntity, VehicleMovementInput> where TPlayerEntity : Vehicle, new()
{
    public EightDirectionVehicleMovementService(IPlayerService<TPlayerEntity> playerService, ILogger<BaseMovementService<TPlayerEntity, VehicleMovementInput>> logger, PortalContext hubContext)
        : base(playerService, logger, hubContext) { }

    protected override void ApplyMovement(Body body, TPlayerEntity vehicle, VehicleMovementInput input)
    {
        base.ApplyMovement(body, vehicle, input);
        HandleTurboFuel(vehicle, input.Turbo);
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

public abstract class ForwardSpacehipMovementService<TPlayerEntity> : ForwardMovementService<TPlayerEntity, SpaceshipMovementInput> where TPlayerEntity : Spaceship, new()
{
    protected ForwardSpacehipMovementService(IPlayerService<TPlayerEntity> playerService, ILogger<BaseMovementService<TPlayerEntity, SpaceshipMovementInput>> logger, PortalContext hubContext) : base(playerService, logger, hubContext)
    {
    }

    protected override void ApplyMovement(Body body, TPlayerEntity vehicle, SpaceshipMovementInput input)
    {
        base.ApplyMovement(body, vehicle, input);
        HandleTurboFuel(vehicle, input.Turbo);
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
