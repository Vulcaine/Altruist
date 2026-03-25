using Altruist;

/// <summary>
/// 3D combat arena with AI monsters, auto-sync, and visibility.
/// Demonstrates: WorldObject3D, [Synchronized], [AIBehavior], ICombatService,
/// VisibilityTracker3D, SpatialCollisionDispatcher, KinematicCharacterController3D.
///
/// Players connect via TCP. Monsters spawn in the world with aggressive AI.
/// Combat uses sweep queries (sphere/cone/line). State syncs automatically.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
        => await AltruistApplication.Run(args);
}
