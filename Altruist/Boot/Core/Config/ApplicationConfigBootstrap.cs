namespace Altruist
{
    // Options/AltruistOptions.cs
    using System.Numerics;

    public sealed class AltruistOptions
    {
        public ServerOptions Server { get; set; } = new();
        public EngineOptions Engine { get; set; } = new();
        public TransportOptions Transport { get; set; } = new();
        public List<World2DOptions> Worlds2D { get; set; } = new(); // optional
        public List<World3DOptions> Worlds3D { get; set; } = new(); // optional
    }

    public sealed class ServerOptions
    {
        public string Host { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 5000;
    }

    public sealed class EngineOptions
    {
        // "2D" or "3D" (defaults to 2D)
        public string Dimension { get; set; } = "2D";

        // Hz (e.g., 30, 60); maps to your FrameRate/HZ rate
        public int FramerateHz { get; set; } = 60;

        // "Ticks" | "Seconds"
        public string Unit { get; set; } = "Ticks";

        // Optional throttle
        public int? Throttle { get; set; }
    }

    public sealed class TransportOptions
    {
        public string Mode { get; set; } = "websocket";
    }

    public sealed class World2DOptions
    {
        public int Index { get; set; }
        public Vector2 Size { get; set; }
        public Vector2 Gravity { get; set; } = Vector2.Zero;
        public float FixedDeltaTime { get; set; } = 1f / 60f;
    }

    public sealed class World3DOptions
    {
        public int Index { get; set; }
        public System.Numerics.Vector3 Size { get; set; }
        public System.Numerics.Vector3 Gravity { get; set; } = System.Numerics.Vector3.Zero;
        public float FixedDeltaTime { get; set; } = 1f / 60f;
    }

}