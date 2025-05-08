
using System.Numerics;

namespace Altruist.Physx;

public class MovementHelper
{
    public static Vector2 GetDirectionVector(float rotation) => new Vector2((float)Math.Cos(rotation), (float)Math.Sin(rotation));
}