namespace Altruist.Gaming;


/// <summary>A typed key naming a kind of world object (players, items, the game's own kinds).</summary>
/// <param name="Value">The key string.</param>
public record WorldObjectTypeKey(string Value);

/// <summary>Built-in <see cref="WorldObjectTypeKey"/> values; define your own the same way for game kinds.</summary>
public static class WorldObjectTypeKeys
{
    /// <summary>A connected client's (player's) object: <c>client</c>.</summary>
    public static readonly WorldObjectTypeKey Client = new("client");
    /// <summary>An item object: <c>item</c>.</summary>
    public static readonly WorldObjectTypeKey Item = new("item");
}
