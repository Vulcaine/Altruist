using System.Reflection;
using System.Text.Json;
using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Gaming.TwoD;
using Altruist.Numerics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.World.ThreeD;

public class WorldLoader3DShapeAndModeTests
{
    private const string Json = """
    {
      "transform": { "position": {"X":0,"Y":0,"Z":0}, "rotation": {"X":0,"Y":0,"Z":0},
                     "scale": {"X":1,"Y":1,"Z":1}, "size": {"X":64,"Y":64,"Z":64} },
      "objects": [
        { "id": "ball-1", "type": "Static", "archetype": "loader-shape-test-ball",
          "position": {"X":4,"Y":0,"Z":4}, "rotation": {"X":0,"Y":0,"Z":0},
          "scale": {"X":1,"Y":1,"Z":1}, "size": {"X":4,"Y":4,"Z":4},
          "colliders": [ { "shape": "Sphere", "radius": 2 } ] }
      ]
    }
    """;

    [Fact]
    public async Task Capitalized_shape_names_are_sized_like_lowercase_ones()
    {
        var index = new Mock<IWorldIndex3D>();
        index.SetupGet(i => i.Index).Returns(0);
        index.SetupGet(i => i.Name).Returns("test");
        index.SetupProperty(i => i.Size, new IntVector3(64, 64, 64));
        var loader = new WorldLoader3D(new WorldPartitioner3D(64, 64, 64), new JsonSerializerOptions(), new VirtualIdAllocator(), physicsEnabled: false);

        await loader.LoadFromJson(index.Object, Json);

        var collider = Assert.Single(Assert.Single(loader.SpawnedWorldObjects).ColliderDescriptors);
        Assert.Equal(2f, collider.Transform.Size.X);
    }

    [Theory]
    [InlineData("2D", true, false)]
    [InlineData("3D", true, true)]
    [InlineData(null, true, false)]
    [InlineData("2D", false, false)]
    [InlineData("3D", false, false)]
    [InlineData(null, false, false)]
    public void Loader_registration_requires_3D_mode_and_game_configuration(string? mode, bool gameConfigured, bool expected)
    {
        var settings = new Dictionary<string, string?>();
        if (mode is not null) settings["altruist:environment:mode"] = mode;
        if (gameConfigured) settings["altruist:game:worlds:partitioner:width"] = "64";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        Assert.Equal(expected, DependencyResolver.ShouldRegister(typeof(WorldLoader3D), config, NullLogger.Instance));
        if (expected)
            Assert.True(DependencyResolver.ShouldRegister(typeof(WorldPartitioner3D), config, NullLogger.Instance));
    }

    [Fact]
    public void Each_force_runtime_is_registered_only_in_its_own_environment_mode()
    {
        static string? Mode(Type t) => t.GetCustomAttributes<ConditionalOnConfigAttribute>(false)
            .SingleOrDefault(c => c.Path == "altruist:environment:mode")?.HavingValue;

        Assert.Equal("2D", Mode(typeof(ForceRuntime2D)));
        Assert.Equal("3D", Mode(typeof(ForceRuntime3D)));
    }
}
