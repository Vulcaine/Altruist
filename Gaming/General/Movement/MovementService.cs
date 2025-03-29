using FarseerPhysics.Dynamics;
using FarseerPhysics.Factories;
using Microsoft.Extensions.Logging;
using Microsoft.Xna.Framework;
using StackExchange.Redis;

namespace Altruist.Gaming;

public abstract class BaseMovementService<TPlayerEntity, MovementInput> where TPlayerEntity : PlayerEntity where MovementInput : Altruist.MovementInput
{
    protected readonly ILogger<BaseMovementService<TPlayerEntity, MovementInput>> _logger;
    protected readonly IPlayerService<TPlayerEntity> _playerService;

    public BaseMovementService(ILogger<BaseMovementService<TPlayerEntity, MovementInput>> logger, PortalContext portalContext)
    {
        _logger = logger;
        _playerService = portalContext.GetPlayerService<TPlayerEntity>();
    }

    public async Task<TPlayerEntity?> MovePlayerAsync(string playerId, MovementInput input)
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
    protected abstract void ApplyRotation(Body body, MovementInput input);

    protected abstract void ApplyMovement(Body body, TPlayerEntity entity, MovementInput input);

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
        return BodyFactory.CreateRectangle(null, 40f, 20f, 1f, new Vector2(entity.Position.X, entity.Position.Y), entity.Rotation);
    }

    protected void UpdateEntityPosition(TPlayerEntity entity, Body body)
    {
        entity.Position = new Vector2(body.Position.X, body.Position.Y);
        entity.Rotation = body.Rotation;
    }

    protected async Task StoreEntityInRedis(string playerId, TPlayerEntity entity)
    {
        var database = ConnectionMultiplexer.Connect("localhost").GetDatabase();
        await database.HashSetAsync(playerId, new[]
        {
            new HashEntry("entity.position.x", entity.Position.X.ToString()),
            new HashEntry("entity.position.y", entity.Position.Y.ToString()),
            new HashEntry("entity.rotation", entity.Rotation.ToString()),
            new HashEntry("entity.currentSpeed", entity.CurrentSpeed.ToString())
        });
    }
}

public abstract class ForwardMovementService<T, MI> : BaseMovementService<T, MI> where T : PlayerEntity where MI : ForwardMovementInput
{
    public ForwardMovementService(ILogger<BaseMovementService<T, MI>> logger, PortalContext hubContext)
        : base(logger, hubContext) { }

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

public abstract class EightDirectionMovementService<T, MI> : BaseMovementService<T, MI> where T : PlayerEntity where MI : EightDirectionMovementInput
{
    public EightDirectionMovementService(ILogger<BaseMovementService<T, MI>> logger, PortalContext hubContext)
        : base(logger, hubContext) { }

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


public abstract class EightDirectionVehicleMovementService<TPlayerEntity> : EightDirectionMovementService<TPlayerEntity, VehicleMovementInput> where TPlayerEntity : Vehicle
{
    public EightDirectionVehicleMovementService(ILogger<BaseMovementService<TPlayerEntity, VehicleMovementInput>> logger, PortalContext hubContext)
        : base(logger, hubContext) { }

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

public abstract class ForwardSpacehipMovementService<TPlayerEntity> : ForwardMovementService<TPlayerEntity, SpaceshipMovementInput> where TPlayerEntity : Spaceship
{
    protected ForwardSpacehipMovementService(ILogger<BaseMovementService<TPlayerEntity, SpaceshipMovementInput>> logger, PortalContext hubContext) : base(logger, hubContext)
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
