using Altruist.Gaming;

namespace Tests.Gaming.AI;

public class StateMotionCurveTests
{
    // The previous implementation: a stable sort of the keys, then a linear scan.
    private static float Reference(StateMotionKey[] keys, float timeN)
    {
        if (keys.Length == 0) return 0f;
        timeN = Math.Clamp(timeN, 0f, 1f);
        var ordered = keys.OrderBy(k => k.TimeN).ToArray();
        if (timeN <= ordered[0].TimeN) return ordered[0].Value;
        for (int i = 1; i < ordered.Length; i++)
        {
            if (timeN > ordered[i].TimeN) continue;
            float span = MathF.Max(0.0001f, ordered[i].TimeN - ordered[i - 1].TimeN);
            float t = (timeN - ordered[i - 1].TimeN) / span;
            return ordered[i - 1].Value + ((ordered[i].Value - ordered[i - 1].Value) * t);
        }
        return ordered[^1].Value;
    }

    [Fact]
    public void Evaluate_matches_a_stable_sort_of_unordered_keys_with_ties()
    {
        var r = new Random(7);
        for (int round = 0; round < 500; round++)
        {
            var keys = Enumerable.Range(0, r.Next(1, 7))
                .Select(_ => new StateMotionKey { TimeN = r.Next(0, 5) / 4f, Value = r.Next(-50, 50) / 7f })
                .ToArray();
            var curve = new StateMotionCurve { Keys = keys };
            for (int s = 0; s <= 20; s++)
            {
                float t = s / 20f - 0.05f;
                Assert.Equal(BitConverter.SingleToInt32Bits(Reference(keys, t)), BitConverter.SingleToInt32Bits(curve.Evaluate(t)));
            }
        }
    }

    [Fact]
    public void Evaluate_does_not_allocate()
    {
        var curve = new StateMotionCurve
        {
            Keys =
            [
                new StateMotionKey { TimeN = 1f, Value = 2f },
                new StateMotionKey { TimeN = 0f, Value = 0f },
                new StateMotionKey { TimeN = 0.5f, Value = 4f },
            ],
        };
        curve.Evaluate(0.3f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float sum = 0;
        for (int i = 0; i < 1000; i++) sum += curve.Evaluate(i / 1000f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(sum > 0);
    }
}
