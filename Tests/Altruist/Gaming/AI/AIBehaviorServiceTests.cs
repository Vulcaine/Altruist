using Altruist.Gaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Gaming.AI;

[Collection("Console")]
public class AIBehaviorServiceTests
{
    [Fact]
    public void Tick_writes_nothing_to_the_console()
    {
        var service = new AIBehaviorService(NullLoggerFactory.Instance);
        var obj = new TestWorldObject();
        var snapshot = new WorldSnapshot(0, [obj], new Dictionary<string, ITypelessWorldObject> { [obj.InstanceId] = obj });
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            service.Tick([snapshot], 0.04f);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.DoesNotContain("[AI-TICK]", captured.ToString());
    }
}
