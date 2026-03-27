using Altruist;

namespace Tests.Framework;

public class ObjectPoolTests
{
    [Fact]
    public void RentList_ReturnsEmptyList()
    {
        var list = AltruistPool.RentList<int>();
        Assert.NotNull(list);
        Assert.Empty(list);
        AltruistPool.ReturnList(list);
    }

    [Fact]
    public void ReturnList_ClearsList()
    {
        var list = AltruistPool.RentList<int>();
        list.Add(1);
        list.Add(2);
        AltruistPool.ReturnList(list);

        var reused = AltruistPool.RentList<int>();
        Assert.Empty(reused);
        AltruistPool.ReturnList(reused);
    }

    [Fact]
    public void RentList_ReusesReturnedInstance()
    {
        var list = AltruistPool.RentList<string>();
        AltruistPool.ReturnList(list);

        var reused = AltruistPool.RentList<string>();
        Assert.Same(list, reused);
        AltruistPool.ReturnList(reused);
    }

    [Fact]
    public void RentDictionary_ReturnsEmptyDict()
    {
        var dict = AltruistPool.RentDictionary<string, int>();
        Assert.NotNull(dict);
        Assert.Empty(dict);
        AltruistPool.ReturnDictionary(dict);
    }

    [Fact]
    public void ReturnDictionary_ClearsDict()
    {
        var dict = AltruistPool.RentDictionary<string, int>();
        dict["a"] = 1;
        AltruistPool.ReturnDictionary(dict);

        var reused = AltruistPool.RentDictionary<string, int>();
        Assert.Empty(reused);
        AltruistPool.ReturnDictionary(reused);
    }

    [Fact]
    public void RentDictionary_WithCapacity_RespectsCapacity()
    {
        var dict = AltruistPool.RentDictionary<string, object?>(capacity: 32);
        Assert.NotNull(dict);
        AltruistPool.ReturnDictionary(dict);
    }

    [Fact]
    public void RentHashSet_ReturnsEmptySet()
    {
        var set = AltruistPool.RentHashSet<string>();
        Assert.NotNull(set);
        Assert.Empty(set);
        AltruistPool.ReturnHashSet(set);
    }

    [Fact]
    public void ReturnHashSet_ClearsSet()
    {
        var set = AltruistPool.RentHashSet<int>();
        set.Add(42);
        AltruistPool.ReturnHashSet(set);

        var reused = AltruistPool.RentHashSet<int>();
        Assert.Empty(reused);
        AltruistPool.ReturnHashSet(reused);
    }

    [Fact]
    public void RentStringBuilder_ReturnsClearedSb()
    {
        var sb = AltruistPool.RentStringBuilder();
        Assert.NotNull(sb);
        Assert.Equal(0, sb.Length);
        AltruistPool.ReturnStringBuilder(sb);
    }

    [Fact]
    public void Pool_IsThreadSafe()
    {
        // Rent and return from multiple threads concurrently
        var tasks = new Task[16];
        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    var list = AltruistPool.RentList<int>();
                    list.Add(i);
                    AltruistPool.ReturnList(list);

                    var dict = AltruistPool.RentDictionary<string, int>();
                    dict["x"] = i;
                    AltruistPool.ReturnDictionary(dict);
                }
            });
        }

        Task.WaitAll(tasks);
        // No exceptions = thread safe
    }
}
