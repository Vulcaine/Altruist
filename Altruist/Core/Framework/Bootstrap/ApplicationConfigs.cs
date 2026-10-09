// Altruist/Options/AltruistOptions.cs
namespace Altruist
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>
    /// Typed snapshot of the whole <c>altruist</c> configuration section, bound once at startup via
    /// <see cref="ConfigurationPropertiesAttribute"/> and injectable as a singleton (only if the section exists).
    /// </summary>
    /// <remarks>
    /// These option classes are read-only snapshots for inspection (dashboards, diagnostics). The framework services
    /// themselves read their keys through <see cref="AppConfigValueAttribute"/>, so changing a value here at runtime has no
    /// effect, and defaults here may differ from the defaults the consuming service applies.
    /// </remarks>
    [ConfigurationProperties("altruist")]
    public sealed class AltruistConfigOptions
    {
        /// <summary><c>altruist:server</c>.</summary>
        public ServerOptions Server { get; set; } = new();

        /// <summary>Bound from <c>altruist:transport</c> (note: not <c>altruist:server:transport</c>; see <see cref="TransportConfigOptions"/> for that).</summary>
        public TransportConfigOptions Transport { get; set; } = new();

        /// <summary><c>altruist:game</c>.</summary>
        public GameConfigOptions Game { get; set; } = new();
    }

    /// <summary>
    /// Snapshot of <c>altruist:server</c> (<c>host</c>, <c>port</c>). The HTTP listener itself is configured by
    /// <c>altruist:server:http:host</c> / <c>altruist:server:http:port</c>, which this class does not cover.
    /// </summary>
    [ConfigurationProperties("altruist:server")]
    public sealed class ServerOptions
    {
        /// <summary><c>altruist:server:host</c>; default <c>0.0.0.0</c>.</summary>
        public string Host { get; set; } = "0.0.0.0";

        /// <summary><c>altruist:server:port</c>; default 5000.</summary>
        public int Port { get; set; } = 5000;
    }

    /// <summary>
    /// Snapshot of <c>altruist:server:transport</c>. Active transports are actually chosen by
    /// <c>altruist:server:transport:{tcp|udp|websocket}:enabled</c>.
    /// </summary>
    [ConfigurationProperties("altruist:server:transport")]
    public sealed class TransportConfigOptions
    {
        /// <summary><c>altruist:server:transport:mode</c>; default <c>websocket</c>. Informational.</summary>
        public string Mode { get; set; } = "websocket";
    }

    /// <summary>Snapshot of <c>altruist:game</c>.</summary>
    [ConfigurationProperties("altruist:game")]
    public sealed class GameConfigOptions
    {
        /// <summary><c>altruist:game:engine</c>.</summary>
        public EngineConfigOptions Engine { get; set; } = new();

        /// <summary><c>altruist:game:worlds</c>.</summary>
        public WorldsOptions Worlds { get; set; } = new();
    }

    /// <summary>
    /// Snapshot of <c>altruist:game:engine</c>. The presence of this section enables the game engine
    /// (engine services are gated with <c>[ConditionalOnConfig("altruist:game:engine")]</c>).
    /// </summary>
    [ConfigurationProperties("altruist:game:engine")]
    public sealed class EngineConfigOptions
    {
        /// <summary><c>diagnostics</c>: enables engine diagnostics.</summary>
        public bool Diagnostics { get; set; } = false;

        /// <summary><c>framerateHz</c>: engine loop frequency in Hz. Default here 60; the engine itself defaults to 30 when the key is absent.</summary>
        public int FramerateHz { get; set; } = 60;

        /// <summary><c>unit</c>: unit used for engine cycle scheduling (e.g. <c>Ticks</c>).</summary>
        public string Unit { get; set; } = "Ticks";

        /// <summary><c>throttle</c>: optional engine throttle value; null when unset.</summary>
        public int? Throttle { get; set; }

        /// <summary><c>gravity</c>: global gravity vector (<c>x</c>/<c>y</c>/<c>z</c> children).</summary>
        public Vector3 Gravity { get; set; }
    }

    /// <summary>Snapshot of <c>altruist:game:worlds</c>.</summary>
    [ConfigurationProperties("altruist:game:worlds")]
    public sealed class WorldsOptions
    {
        /// <summary><c>partitioner</c>: spatial partition cell size.</summary>
        public PartitionerOptions Partitioner { get; set; } = new();

        /// <summary><c>items</c>: one entry per world.</summary>
        public List<WorldOptions> Items { get; set; } = new();
    }

    /// <summary>Snapshot of <c>altruist:game:worlds:partitioner</c>: partition cell dimensions in world units.</summary>
    [ConfigurationProperties("altruist:game:worlds:partitioner")]
    public sealed class PartitionerOptions
    {
        /// <summary>Cell width; default 64.</summary>
        public int Width { get; set; } = 64;

        /// <summary>Cell height; default 64.</summary>
        public int Height { get; set; } = 64;

        /// <summary>Cell depth (3D only); default 64.</summary>
        public int? Depth { get; set; } = 64;
    }

    /// <summary>
    /// One world entry of <c>altruist:game:worlds:items</c>. Because that section is a list, it is registered as
    /// <c>List&lt;WorldOptions&gt;</c> / <c>IEnumerable&lt;WorldOptions&gt;</c> / <c>IReadOnlyList&lt;WorldOptions&gt;</c>
    /// (see <see cref="ConfigurationPropertiesAttribute"/>). World services are created per item by a list-style
    /// <see cref="ConditionalOnConfigAttribute"/> keyed by <c>id</c>.
    /// </summary>
    [ConfigurationProperties("altruist:game:worlds:items")]
    public sealed class WorldOptions
    {
        /// <summary><c>index</c>: numeric world index.</summary>
        public int Index { get; set; }

        /// <summary><c>id</c>: world key (also the keyed-service key).</summary>
        public string? Id { get; set; }

        /// <summary><c>name</c>: display name.</summary>
        public string? Name { get; set; }

        /// <summary><c>data</c>: optional world data reference. Note: the world services read <c>data-path</c>, not this key.</summary>
        public string? Data { get; set; } = null;

        /// <summary><c>size</c>: world extent; a <c>z</c> component makes the world 3D.</summary>
        public VectorConfig Size { get; set; } = new();

        /// <summary><c>gravity</c>: world gravity vector.</summary>
        public VectorConfig Gravity { get; set; } = new();

        /// <summary><c>position</c>: world origin.</summary>
        public VectorConfig Position { get; set; } = new();

        /// <summary><c>fixedDeltaTime</c>: world physics step in seconds; null when unset.</summary>
        public float? FixedDeltaTime { get; set; }

        /// <summary>True when any of <see cref="Size"/>, <see cref="Gravity"/> or <see cref="Position"/> has a <c>z</c> component.</summary>
        public bool Is3D => Size.Z.HasValue || Gravity.Z.HasValue || Position.Z.HasValue;
    }

    /// <summary>A 2D or 3D vector as written in configuration (<c>x</c>, <c>y</c>, optional <c>z</c>).</summary>
    public sealed class VectorConfig
    {
        /// <summary>X component.</summary>
        public float X { get; set; }

        /// <summary>Y component.</summary>
        public float Y { get; set; }

        /// <summary>Z component; null for 2D values.</summary>
        public float? Z { get; set; }

        /// <summary>Returns (X, Y).</summary>
        public Vector2 ToVector2() => new(X, Y);

        /// <summary>Returns (X, Y, Z), using 0 for a missing Z.</summary>
        public Vector3 ToVector3() => new(X, Y, Z ?? 0f);
    }
}
