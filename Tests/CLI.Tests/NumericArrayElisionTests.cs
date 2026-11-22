using System.Text.Json.Nodes;
using Altruist.CLI.Runtime;

namespace Altruist.CLI.Tests;

public sealed class NumericArrayElisionTests
{
    private static string Numbers(int count) => "[" + string.Join(",", Enumerable.Range(0, count)) + "]";

    [Fact]
    public void replaces_a_large_grid_with_its_shape()
    {
        var grid = "[" + string.Join(",", Enumerable.Repeat(Numbers(129), 129)) + "]";

        var result = JsonNode.Parse(NumericArrayElision.Apply($"{{\"heightfield\":{{\"rows\":129,\"heights\":{grid}}}}}"))!;

        Assert.Equal("[129x129: 16,641 numbers left out]", result["heightfield"]!["heights"]!.GetValue<string>());
        Assert.Equal(129, result["heightfield"]!["rows"]!.GetValue<int>());
    }

    [Fact]
    public void replaces_a_long_flat_array_of_numbers()
    {
        var result = JsonNode.Parse(NumericArrayElision.Apply($"{{\"vertices\":{Numbers(300)}}}"))!;

        Assert.Equal("[300: 300 numbers left out]", result["vertices"]!.GetValue<string>());
    }

    [Fact]
    public void keeps_small_numeric_arrays_and_everything_else()
    {
        var json = $"{{\"position\":[1,2,3],\"grid\":{Numbers(256)},\"names\":[\"a\",\"b\"],\"objects\":[{{\"id\":1}}]}}";

        Assert.Equal(JsonNode.Parse(json)!.ToJsonString(), NumericArrayElision.Apply(json));
    }

    [Fact]
    public void returns_a_body_that_is_not_json_unchanged()
    {
        Assert.Equal("not json", NumericArrayElision.Apply("not json"));
    }
}
