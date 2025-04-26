using Altruist.Gaming;

namespace SimpleGame.Entities;

public class SimpleSpaceship : Spaceship
{
    protected override void InitDefaults()
    {
        base.InitDefaults();
        MaxSpeed = 20f;
        MaxTurboSpeed = 30f;
        RotationSpeed = 0.5f;
        Acceleration = 10f;
        MaxAcceleration = 20f;
        Deceleration = 10f;
        MaxDeceleration = 20f;
        TurboFuel = 100f;
        MaxTurboFuel = 100f;
        ToggleTurbo = false;
        EngineQuality = 1f;
        ShootSpeed = 5f;
    }
}
