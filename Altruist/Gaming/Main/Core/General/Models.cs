namespace Altruist.Gaming;


public record WorldObjectTypeKey(string Value);

public static class WorldObjectTypeKeys
{
    public static readonly WorldObjectTypeKey Client = new("client");
    public static readonly WorldObjectTypeKey Item = new("item");
}
