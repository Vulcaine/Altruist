namespace Altruist.Dashboard;

[Service]
public sealed class DashboardWorldViewOptions
{
    public DashboardWorldViewOptions(
        [AppConfigValue("altruist:dashboard:world:renderScale", "1")]
        float renderScale,
        [AppConfigValue("altruist:dashboard:world:terrainSampleStride", "1")]
        int terrainSampleStride)
    {
        RenderScale = Math.Clamp(renderScale, 0.01f, 100f);
        TerrainSampleStride = Math.Clamp(terrainSampleStride, 1, 128);
    }

    public float RenderScale { get; }
    public int TerrainSampleStride { get; }
}
