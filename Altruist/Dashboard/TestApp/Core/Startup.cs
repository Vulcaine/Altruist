using System.Numerics;

using Altruist;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;
using Altruist.ThreeD.Numerics;

[AltruistModule]
public static class ServerModule
{
    [AltruistModuleLoader]
    public static void Initialize(
        IGameWorldOrganizer3D worldOrganizer,
        IHeightmapLoader heightmapLoader)
    {
        var heightmapFile = "Resources/Heightmaps/Land_heightmap.hmap";
        var heightmapData = heightmapLoader.RAW.LoadHeightmap(heightmapFile);
        var allWorlds = worldOrganizer.GetAllWorlds();

        foreach (var world in allWorlds)
        {
            var worldSize = world.Index.Size;
            var origin = new IntVector3(0, 0, 0);
            var size = new Vector3(
                x: worldSize.X,
                y: heightmapData.HeightScale,
                z: worldSize.Z
            );

            var transform = Transform3D.From(
                origin: origin,
                rotation: Quaternion.Identity,
                size: size
            );

            world.SpawnStaticObject(new Terrain(transform, heightmapData));
        }
    }
}
