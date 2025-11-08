// AltruistConfigOptions.cs
using System.Numerics;

namespace Altruist
{
    /// <summary>
    /// Root options bound from config.yml -> altruist:*
    /// </summary>
    public sealed class AltruistConfigOptions
    {
        public ServerConfigOptions Server { get; set; } = new();
        public TransportConfigOptions Transport { get; set; } = new();

        /// <summary>
        /// Game-related options (engine + worlds). If present (and non-empty), Boot will request the game-engine feature.
        /// </summary>
        public GameConfigOptions? Game { get; set; }
    }

    public sealed class ServerConfigOptions
    {
        public string Host { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 5000;
    }

    public sealed class TransportConfigOptions
    {
        /// <summary> e.g., "websocket" </summary>
        public string Mode { get; set; } = "websocket";
    }

    /// <summary>
    /// Groups engine + worlds under 'altruist:game'
    /// </summary>
    public sealed class GameConfigOptions
    {
        public EngineConfigOptions Engine { get; set; } = new();

        /// <summary>
        /// Single unified worlds list. If any world provides Z (size or gravity), all must be 3D.
        /// </summary>
        public List<WorldConfigOptions> Worlds { get; set; } = new();
    }

    public sealed class EngineConfigOptions
    {
        /// <summary>"2D", "3D", or "Auto" (default). If Auto, infer from worlds (presence of Z).</summary>
        public string Dimension { get; set; } = "Auto";

        /// <summary>Target frequency in Hz (e.g. 30, 60).</summary>
        public int FramerateHz { get; set; } = 60;

        /// <summary>"Ticks" or "Seconds".</summary>
        public string Unit { get; set; } = "Ticks";

        /// <summary>Optional throttle.</summary>
        public int? Throttle { get; set; }
    }

    /// <summary>
    /// Unified world config. If Size.Z or Gravity.Z is present, the world is considered 3D.
    /// </summary>
    public sealed class WorldConfigOptions
    {
        public int Index { get; set; }
        public VectorConfig Size { get; set; } = new();
        public VectorConfig Gravity { get; set; } = new();
        public VectorConfig Position { get; set; } = new();

        public bool Is3D => Size.Z.HasValue || Gravity.Z.HasValue;
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
