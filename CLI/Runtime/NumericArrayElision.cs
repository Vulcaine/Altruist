using System.Text.Json;
using System.Text.Json.Nodes;

namespace Altruist.CLI.Runtime;

/// <summary>
/// Replaces large arrays of numbers in a dashboard answer (terrain heightfields, mesh vertices) with their shape: an
/// agent reads objects and properties, and thousands of samples would crowd them out of its context.
/// </summary>
public static class NumericArrayElision
{
    // A 16x16 grid still comes through whole; a 129x129 terrain does not.
    public const int MaxNumbers = 256;

    /// <summary>The JSON with every numeric array of more than <see cref="MaxNumbers"/> numbers replaced by a description; a body that is not JSON is returned as it is.</summary>
    public static string Apply(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }
        return root is null ? json : Elide(root).ToJsonString();
    }

    private static JsonNode Elide(JsonNode node) => node switch
    {
        JsonArray array when Shape(array) is { } shape && shape.Count > MaxNumbers =>
            JsonValue.Create($"[{shape.Description}: {shape.Count:N0} numbers left out]"),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Elide(item)).ToArray()),
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Elide(p.Value)))),
        _ => node.DeepClone(),
    };

    private sealed record NumericShape(int Count, string Description);

    // The shape of an array of numbers or of equally long arrays of numbers; absent for anything else.
    private static NumericShape? Shape(JsonArray array)
    {
        if (array.Count > 0 && array.All(IsNumber))
            return new NumericShape(array.Count, $"{array.Count}");
        if (array.Count == 0 || !array.All(item => item is JsonArray row && row.Count > 0 && row.All(IsNumber)))
            return null;
        var lengths = array.Select(item => ((JsonArray)item!).Count).Distinct().ToArray();
        var count = array.Sum(item => ((JsonArray)item!).Count);
        return new NumericShape(count, lengths.Length == 1 ? $"{array.Count}x{lengths[0]}" : $"{array.Count} rows");
    }

    private static bool IsNumber(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number;
}
