// Altruist/Options/AltruistOptions.cs
namespace Altruist
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ConfigurationPropertiesAttribute : Attribute
    {
        public string Path { get; }
        public ConfigurationPropertiesAttribute(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    [ConfigurationProperties("altruist")]
    public sealed class AltruistConfigOptions
    {
        public ServerOptions Server { get; set; } = new();
        public TransportConfigOptions Transport { get; set; } = new();
        public GameConfigOptions Game { get; set; } = new();
    }

    [ConfigurationProperties("altruist:server")]
    public sealed class ServerOptions
    {
        public string Host { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 5000;
    }

    [ConfigurationProperties("altruist:server:transport")]
    public sealed class TransportConfigOptions
    {
        public string Mode { get; set; } = "websocket";
    }

    [ConfigurationProperties("altruist:game")]
    public sealed class GameConfigOptions
    {
        public EngineConfigOptions Engine { get; set; } = new();
        public WorldsOptions Worlds { get; set; } = new();
    }

    [ConfigurationProperties("altruist:game:engine")]
    public sealed class EngineConfigOptions
    {
        public int FramerateHz { get; set; } = 60;
        public string Unit { get; set; } = "Ticks";
        public int? Throttle { get; set; }
        public Vector3 Gravity { get; set; }
    }

    [ConfigurationProperties("altruist:game:worlds")]
    public sealed class WorldsOptions
    {
        public PartitionerOptions Partitioner { get; set; } = new();
        public List<WorldOptions> Items { get; set; } = new();
    }

    [ConfigurationProperties("altruist:game:worlds:partitioner")]
    public sealed class PartitionerOptions
    {
        public int Width { get; set; } = 64;
        public int Height { get; set; } = 64;
        public int? Depth { get; set; } = 64;
    }

    [ConfigurationProperties("altruist:game:worlds:items")]
    public sealed class WorldOptions
    {
        public int Index { get; set; }
        public string? Data { get; set; } = null;
        public VectorConfig Size { get; set; } = new();
        public VectorConfig Gravity { get; set; } = new();
        public VectorConfig Position { get; set; } = new();
        public float? FixedDeltaTime { get; set; }
        public bool Is3D => Size.Z.HasValue || Gravity.Z.HasValue || Position.Z.HasValue;
    }

    public sealed class VectorConfig
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float? Z { get; set; }

        public Vector2 ToVector2() => new(X, Y);
        public Vector3 ToVector3() => new(X, Y, Z ?? 0f);
    }
}
