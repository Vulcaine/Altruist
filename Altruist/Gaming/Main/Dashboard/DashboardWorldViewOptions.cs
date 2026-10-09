namespace Altruist.Dashboard;

/// <summary>
/// Render hints for the world dashboard viewer, read from config and sent with every world snapshot
/// (<see cref="WorldObjectsSnapshotDto.RenderOptions"/>). Singleton DI service.
/// </summary>
/// <remarks>
/// Config keys: <c>altruist:dashboard:world:renderScale</c> (default 1, clamped 0.01..100) and
/// <c>altruist:dashboard:world:terrainSampleStride</c> (default 1, clamped 1..128). Raise the stride for large heightfields
/// to shrink snapshot payloads.
/// </remarks>
[Service]
public sealed class DashboardWorldViewOptions
{
    /// <summary>Created by DI from config values.</summary>
    /// <param name="renderScale">Value of <c>altruist:dashboard:world:renderScale</c>.</param>
    /// <param name="terrainSampleStride">Value of <c>altruist:dashboard:world:terrainSampleStride</c>.</param>
    public DashboardWorldViewOptions(
        [AppConfigValue("altruist:dashboard:world:renderScale", "1")]
        float renderScale,
        [AppConfigValue("altruist:dashboard:world:terrainSampleStride", "1")]
        int terrainSampleStride)
    {
        RenderScale = Math.Clamp(renderScale, 0.01f, 100f);
        TerrainSampleStride = Math.Clamp(terrainSampleStride, 1, 128);
    }

    /// <summary>Scale factor the viewer applies to world units when rendering (0.01..100).</summary>
    public float RenderScale { get; }
    /// <summary>Heightfield down-sampling stride for snapshots: every Nth sample is sent on each axis (1..128).</summary>
    public int TerrainSampleStride { get; }
}
