using Altruist.Engine;

namespace Tests.Gaming.Engine;

public class EngineWithDiagnosticsTests
{
    [Fact]
    public void Diagnostics_engine_is_a_top_level_type()
    {
        var type = typeof(EngineWithoutDiagnostics).Assembly.GetType("Altruist.Engine.EngineWithDiagnostics");

        Assert.NotNull(type);
        Assert.False(type!.IsNested);
    }
}
