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
    public sealed class AltruistOptions
    {
        public ServerOptions Server { get; set; } = new();
        public TransportOptions Transport { get; set; } = new();
        public GameOptions Game { get; set; } = new();
    }

    [ConfigurationProperties("altruist:server")]
    public sealed class ServerOptions
    {
        public string Host { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 5000;
    }

    [ConfigurationProperties("altruist:transport")]
    public sealed class TransportOptions
    {
        public string Mode { get; set; } = "websocket";
    }

    [ConfigurationProperties("altruist:game")]
    public sealed class GameOptions
    {
        public EngineOptions Engine { get; set; } = new();
        public WorldsOptions Worlds { get; set; } = new();
    }

    [ConfigurationProperties("altruist:game:engine")]
    public sealed class EngineOptions
    {
        public string Dimension { get; set; } = "2D";
        public int FramerateHz { get; set; } = 60;
        public string Unit { get; set; } = "Ticks";
        public int? Throttle { get; set; }
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
        public int PartitionWidth { get; set; } = 64;
        public int PartitionHeight { get; set; } = 64;
        public int? PartitionDepth { get; set; } = 64;
    }

    [ConfigurationProperties("altruist:game:worlds:items")]
    public sealed class WorldOptions
    {
        public int Index { get; set; }
        public VectorConfig Size { get; set; } = new();
        public VectorConfig Gravity { get; set; } = new();
        public VectorConfig Position { get; set; } = new();
        public float? FixedDeltaTime { get; set; }
        public bool Is3D => Size.Z.HasValue || Gravity.Z.HasValue || Position.Z.HasValue;
    }
}
