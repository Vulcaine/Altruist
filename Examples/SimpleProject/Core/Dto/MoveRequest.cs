using System.Numerics;

namespace SimpleGame;

public class MoveRequest3D
{
    public bool? Jump;
    public bool? MoveUp;
    public bool? MoveDown;
    public bool? MoveLeft;
    public bool? MoveRight;

    public bool? RotateLeft;
    public bool? RotateRight;

    /// <summary>
    /// World-space movement input. For ground games keep Y=0; for fly/jetpack fill Y too.
    /// </summary>
    public Vector3 GetDirection() => new(
        (MoveRight == true ? 1f : 0f) + (MoveLeft == true ? -1f : 0f),
        (MoveUp == true ? 1f : 0f) + (MoveDown == true ? -1f : 0f),
        0f // use Z if you map forward/back here; keep 0 if you map forward to Y in 2D plane
    );

    /// <summary>
    /// Signed yaw turn scalar (-1..+1). Positive = turn right.
    /// </summary>
    public float GetTurn() =>
        (RotateRight == true ? 1f : 0f) + (RotateLeft == true ? -1f : 0f);

    public bool WantsJump() => Jump == true;
}
