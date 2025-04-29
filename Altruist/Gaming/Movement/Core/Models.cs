namespace Altruist.Gaming.Movement;



public abstract class MovementInput
{
    public float RotationSpeed { get; set; } = 0f;
    public bool Turbo { get; set; }
}



public class VehicleMovementInput : EightDirectionMovementInput
{

}


public class SpaceshipMovementInput : ForwardMovementInput
{

}



public class ForwardMovementInput : MovementInput
{
    public bool MoveUp { get; set; }
    public bool RotateLeft { get; set; }
    public bool RotateRight { get; set; }
}


public class EightDirectionMovementInput : MovementInput
{
    public bool MoveUp { get; set; }
    public bool MoveDown { get; set; }
    public bool MoveLeft { get; set; }
    public bool MoveRight { get; set; }
}
