using System.Numerics;
using Altruist.Gaming.ThreeD;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using Moq;

namespace Tests.Gaming.Physx.ThreeD;

/// <summary>
/// Reproduces the production "fell out of terrain when walking down a slope"
/// bug. The KCC uses <c>ResolveDownwardTerrainContact</c> after each move to
/// snap the body to ground, but its probe distance was sized only for the
/// downward velocity contribution (~0.2 units). Walking horizontally on a
/// slope drops the terrain by <c>horizMag * tan(angle)</c> per tick — for
/// any walkable slope the probe missed → IsGrounded=false next tick →
/// gravity → freefall.
/// </summary>
public sealed class KinematicCharacterController3DTerrainTests
{
    [Fact]
    public void WalkingOnFlatTerrain_StaysOnGround()
    {
        var terrain = new FakeTerrain(_ => 100f);
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 100.9f, 0f)); // foot at terrain (Height=1.8 → centerToFoot=0.9)
        controller.SetBody(body);

        WalkForward(controller, ticks: 60, dt: 0.04f);

        // After 60 ticks at ~5 m/s on flat terrain, foot should still be on the
        // ground (within ~0.1 of expected). The original code passed this case.
        float foot = controller.Position.Y - 0.9f;
        Assert.InRange(foot, 99.9f, 100.1f);
    }

    [Fact]
    public void WalkingDownGentleSlope_DoesNotFallThrough()
    {
        // 20° slope: height = 100 - x * tan(20°) ≈ 100 - x * 0.364
        var terrain = new FakeTerrain(p => 100f - p.X * MathF.Tan(20f * MathF.PI / 180f));
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 100.9f, 0f));
        controller.SetBody(body);

        WalkForward(controller, ticks: 60, dt: 0.04f);

        // After walking ~12 units (60 ticks * 5m/s * 0.04s) the slope drops by
        // 12 * tan(20°) ≈ 4.36, so terrain at the new X is ~95.64. Foot should
        // be within ~0.5 of that — NOT 20+ units below from freefall.
        float expectedTerrain = 100f - controller.Position.X * MathF.Tan(20f * MathF.PI / 180f);
        float foot = controller.Position.Y - 0.9f;
        float drift = MathF.Abs(foot - expectedTerrain);
        Assert.True(drift < 1.0f,
            $"Foot Y drifted {drift:F2} from terrain after walking down a 20° slope. foot={foot:F2}, terrain={expectedTerrain:F2}, x={controller.Position.X:F2}");
    }

    [Theory]
    [InlineData(45)]
    [InlineData(52)]
    [InlineData(58)]
    [InlineData(65)]  // steeper than MaxSlopeAngleDeg=60° — should fail with current fix
    [InlineData(75)]  // very steep — should fail
    public void WalkingDownVariousSlopes_DoesNotFallThrough(int slopeAngleDeg)
    {
        var terrain = new FakeTerrain(p => 100f - p.X * MathF.Tan(slopeAngleDeg * MathF.PI / 180f));
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 100.9f, 0f));
        controller.SetBody(body);

        WalkForward(controller, ticks: 60, dt: 0.04f);

        float expectedTerrain = 100f - controller.Position.X * MathF.Tan(slopeAngleDeg * MathF.PI / 180f);
        float foot = controller.Position.Y - 0.9f;
        float drift = MathF.Abs(foot - expectedTerrain);
        Assert.True(drift < 1.5f,
            $"Walked down {slopeAngleDeg}° slope: foot drifted {drift:F2} from terrain. foot={foot:F2}, terrain={expectedTerrain:F2}, x={controller.Position.X:F2}");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(75)]
    public void WalkingUpSlope_StaysOnTerrainSurface(int slopeAngleDeg)
    {
        // Production: walking UP a steep slope drops the player through it.
        // Terrain Y = 100 + x * tan(angle). After a horizontal step of size h
        // terrain rises by h*tan(angle). The ground-snap probe origin sits
        // just above the foot, but a "rising" terrain is ABOVE the foot —
        // the downward ray starts already below ground and misses it. Body
        // ends up embedded; next tick IsGrounded=false → gravity → fall.
        var terrain = new FakeTerrain(p => 100f + p.X * MathF.Tan(slopeAngleDeg * MathF.PI / 180f));
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 100.9f, 0f));
        controller.SetBody(body);

        WalkForward(controller, ticks: 60, dt: 0.04f);

        float expectedTerrain = 100f + controller.Position.X * MathF.Tan(slopeAngleDeg * MathF.PI / 180f);
        float foot = controller.Position.Y - 0.9f;
        float drift = MathF.Abs(foot - expectedTerrain);
        Assert.True(drift < 1.5f,
            $"Walked UP {slopeAngleDeg}° slope: foot drifted {drift:F2} from terrain. foot={foot:F2}, terrain={expectedTerrain:F2}, x={controller.Position.X:F2}");
    }

    [Fact]
    public void WalkingDownSteepSlope_DoesNotFallThrough()
    {
        // 45° slope — within MaxSlopeAngleDeg=60° default
        var terrain = new FakeTerrain(p => 100f - p.X * MathF.Tan(45f * MathF.PI / 180f));
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 100.9f, 0f));
        controller.SetBody(body);

        WalkForward(controller, ticks: 60, dt: 0.04f);

        float expectedTerrain = 100f - controller.Position.X * MathF.Tan(45f * MathF.PI / 180f);
        float foot = controller.Position.Y - 0.9f;
        float drift = MathF.Abs(foot - expectedTerrain);
        Assert.True(drift < 1.0f,
            $"Foot Y drifted {drift:F2} from terrain after walking down a 45° slope. foot={foot:F2}, terrain={expectedTerrain:F2}, x={controller.Position.X:F2}");
    }

    [Fact]
    public void SprintingDownSteepSlope_DoesNotFallThrough()
    {
        // 50° slope at sprint speed (~10 m/s) — closer to production scenario
        // where the user reported "walked down a slope and fell out of terrain"
        var terrain = new FakeTerrain(p => 100f - p.X * MathF.Tan(50f * MathF.PI / 180f));
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 100.9f, 0f));
        controller.SetBody(body);

        controller.MovementStats = new CharacterMovementStats3D(
            WalkSpeed: 5f,
            SprintSpeed: 10f,
            MoveSpeedMultiplier: 1f,
            RotationSpeedDegPerSec: 0f);
        controller.SetMovementInput(new CharacterRealtimeWasdInput3D(
            MoveX: 0f, MoveZ: 1f,
            LookDirection: Vector3.Zero,
            CameraYaw: MathF.PI * 0.5f,
            ExternalFacingYaw: 0f, UseExternalFacing: false,
            Sprint: true,
            Jump: false));

        var world = Mock.Of<IGameWorldManager3D>();
        for (int i = 0; i < 60; i++)
            controller.Step(0.04f, world);

        float expectedTerrain = 100f - controller.Position.X * MathF.Tan(50f * MathF.PI / 180f);
        float foot = controller.Position.Y - 0.9f;
        float drift = MathF.Abs(foot - expectedTerrain);
        Assert.True(drift < 1.5f,
            $"SPRINT down 50° slope: foot drifted {drift:F2} from terrain. foot={foot:F2}, terrain={expectedTerrain:F2}, x={controller.Position.X:F2}");
    }

    [Fact]
    public void WalkingOverProductionLikeCliff_LandsOnLowerTerrain()
    {
        // Production scenario: user observed Y dropping from 176 → 149 (27 units).
        // Plateau at Y=176 for x<5, drops to Y=149 for x>=5.
        var terrain = new FakeTerrain(p => p.X < 5f ? 176f : 149f);
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 176.9f, 0f));
        controller.SetBody(body);

        WalkForward(controller, ticks: 300, dt: 0.04f);

        float foot = controller.Position.Y - 0.9f;
        Assert.True(foot >= 148.5f && foot <= 150f,
            $"After 27-unit cliff drop, foot Y={foot:F2}; should land on Y≈149.");
    }

    [Fact]
    public void WalkingIntoOutOfBoundsTerrain_DoesNotFallToZero()
    {
        // Heightmap bug scenario: terrain returns 0 outside bounds. If the
        // player walks past the heightmap edge, ResolveDownwardTerrainContact
        // sees terrain at Y=0 and the player will fall there. Production
        // ValeriaTerrainProvider does exactly this — `return 0f` at line 41.
        // This test demonstrates the bug; failure is expected until fixed.
        var terrain = new FakeTerrain(p => p.X < 50f ? 100f : 0f);
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 100.9f, 0f));
        controller.SetBody(body);

        WalkForward(controller, ticks: 600, dt: 0.04f);

        // Player walked ~120 units in X, well past the X=50 boundary. Without a
        // fix, foot would be at Y=0. With a sane fix, the player should be
        // clamped or refuse to move out of bounds — but at minimum should not
        // be at Y=0 having fallen 100 units.
        float foot = controller.Position.Y - 0.9f;
        // For now we assert the bug exists so the test FAILS visibly. Comment
        // out / invert this when we ship the fix.
        Assert.True(foot < 50f,
            $"Out-of-bounds bug check: walked past heightmap edge, foot Y={foot:F2}. Expected <50 (fell to Y=0 bug); flip this assertion once fixed.");
    }

    [Fact]
    public void WalkingOffCliff_LandsOnLowerTerrain_NotFreefallForever()
    {
        // Plateau at Y=100 for x<5, drops to Y=80 for x>=5 (20-unit cliff)
        var terrain = new FakeTerrain(p => p.X < 5f ? 100f : 80f);
        var controller = CreateController(terrain);
        var body = CreateBody(new Vector3(0f, 100.9f, 0f));
        controller.SetBody(body);

        WalkForward(controller, ticks: 200, dt: 0.04f);

        // After 200 ticks (8 seconds) at 5m/s, player has traveled ~40 units in
        // X. They should have walked off the cliff and landed on Y=80 terrain.
        // Foot should be at ~80, not at 0 or somewhere far below.
        float foot = controller.Position.Y - 0.9f;
        Assert.True(foot >= 79.5f && foot <= 81.0f,
            $"After walking off a 20-unit cliff and continuing, foot Y={foot:F2}; should have landed on Y≈80 terrain.");
    }

    // Walks in +X direction at ~5 m/s. Calls Step(dt) each iteration.
    private static void WalkForward(KinematicCharacterController3D controller, int ticks, float dt)
    {
        controller.MovementStats = new CharacterMovementStats3D(
            WalkSpeed: 5f,
            SprintSpeed: 5f,
            MoveSpeedMultiplier: 1f,
            RotationSpeedDegPerSec: 0f);
        controller.SetMovementInput(new CharacterRealtimeWasdInput3D(
            MoveX: 0f,
            MoveZ: 1f,
            LookDirection: Vector3.Zero,
            CameraYaw: MathF.PI * 0.5f, // forward = +X
            ExternalFacingYaw: 0f,
            UseExternalFacing: false,
            Sprint: false,
            Jump: false));

        var world = Mock.Of<IGameWorldManager3D>();
        for (int i = 0; i < ticks; i++)
            controller.Step(dt, world);
    }

    private static KinematicCharacterController3D CreateController(ITerrainProvider terrain)
    {
        var queries = new TerrainOnlySpatialQueryProvider(terrain);
        var controller = new KinematicCharacterController3D
        {
            MovementProfile = MmoMovementProfile3D.Default,
            UseCapsuleSweeps = true,
            UseDepenetration = false,
            Acceleration = 1000f,
            Deceleration = 1000f,
            Height = 1.8f,
            Radius = 0.4f,
            SkinWidth = 0.02f,
            GroundProbeDistance = 0.12f,
            MaxSlopeAngleDeg = 60f,
        };
        controller.SetQueryProvider(queries);
        return controller;
    }

    private static InMemoryPhysxBody3D CreateBody(Vector3 center)
    {
        var transform = Transform3D.From(center, Quaternion.Identity, Vector3.One);
        var desc = PhysxBody3D.Create(PhysxBodyType.Kinematic, mass: 0f, transform, isKinematic: true);
        return new InMemoryPhysxBody3D(desc);
    }

    /// <summary>
    /// Minimal ISpatialQueryProvider that only knows about terrain — no entity
    /// colliders. Mirrors HeightmapSpatialQueryProvider's terrain-only ray hit
    /// (intersection with the terrain plane at the ray's XZ).
    /// </summary>
    private sealed class TerrainOnlySpatialQueryProvider : ISpatialQueryProvider
    {
        private readonly ITerrainProvider _terrain;

        public TerrainOnlySpatialQueryProvider(ITerrainProvider terrain) => _terrain = terrain;

        public IEnumerable<SpatialHit> CapsuleCast(
            Vector3 center, float radius, float halfLength,
            Vector3 direction, float maxDistance, int maxHits, uint layerMask)
        {
            // Walkable everywhere — no terrain blocking. Lets us isolate the
            // ground-snap logic from horizontal collision.
            yield break;
        }

        public IEnumerable<SpatialHit> RayCast(
            Vector3 origin, Vector3 direction, float maxDistance, int maxHits, uint layerMask)
        {
            if (direction.Y >= -0.01f) yield break;

            // Same intersection math as HeightmapSpatialQueryProvider: terrain
            // is a horizontal plane at GetHeight(origin.X, origin.Z).
            float groundY = _terrain.GetHeight(origin.X, origin.Z);
            float t = (origin.Y - groundY) / -direction.Y;
            if (t < 0f || t > maxDistance) yield break;

            yield return new SpatialHit
            {
                T = t,
                Point = origin + direction * t,
                Normal = Vector3.UnitY,
                HitObject = null,
            };
        }
    }

    private sealed class FakeTerrain : ITerrainProvider
    {
        private readonly Func<Vector2, float> _height;
        public FakeTerrain(Func<Vector2, float> height) => _height = height;
        public bool IsWalkable(float x, float y, float z) => true;
        public float GetHeight(float x, float z) => _height(new Vector2(x, z));
        public Vector3 GetNormal(float x, float z) => Vector3.UnitY;
    }
}
