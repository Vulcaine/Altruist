using Altruist;

/// <summary>
/// 2D top-down shooter with world objects, visibility, AI, and auto-sync.
/// Demonstrates: WorldObject2D, [Synchronized], [AIBehavior], VisibilityTracker2D.
///
/// Players connect via TCP, spawn into a 2D world. Enemy drones have AI that
/// chases and attacks players. Position sync is automatic via [Synced] properties.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
        => await AltruistApplication.Run(args);
}
