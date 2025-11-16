using System.Numerics;
using System.Text.Json;

using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.World.ThreeD;

public static class AngleExtensions
{
    public static float ToRadians(this float degrees)
        => MathF.PI / 180f * degrees;
}


public sealed class WorldLoader
{

    public static WorldStaticData LoadFromPath(string path) => LoadFromJson(File.ReadAllText(path));
    public static WorldStaticData LoadFromJson(string jsonContent)
    {
        var raw = JsonSerializer.Deserialize<WorldRaw>(jsonContent)
                  ?? throw new Exception("Invalid world JSON.");

        var world = new WorldStaticData();

        foreach (var o in raw.Objects)
        {
            var obj = new WorldStaticObjectData
            {
                Id = o.Id,
                Type = o.Type,
                Position = o.Position,
                Rotation = Quaternion.CreateFromYawPitchRoll(
                    o.RotationEuler.Y.ToRadians(),
                    o.RotationEuler.X.ToRadians(),
                    o.RotationEuler.Z.ToRadians()),
                Scale = o.Scale,
                Collider = ConvertCollider(o.Collider)
            };

            world.Objects.Add(obj);

            if (obj.Collider != null)
                world.CollisionObjects.Add(obj);
        }

        return world;
    }

    private static WorldStaticColliderData? ConvertCollider(WorldColliderData? raw)
    {
        if (raw == null)
            return null;

        return raw.Shape.ToLower() switch
        {
            "box" => new WorldStaticColliderData
            {
                Shape = PhysxColliderShape3D.Box3D,
                Center = raw.Center ?? Vector3.Zero,
                Size = raw.Size
            },

            "sphere" => new WorldStaticColliderData
            {
                Shape = PhysxColliderShape3D.Sphere3D,
                Center = raw.Center ?? Vector3.Zero,
                Radius = raw.Radius
            },

            "capsule" => new WorldStaticColliderData
            {
                Shape = PhysxColliderShape3D.Capsule3D,
                Center = raw.Center ?? Vector3.Zero,
                Height = raw.Height,
                Radius = raw.Radius,
                Direction = raw.Direction
            },

            _ => null
        };
    }
}
