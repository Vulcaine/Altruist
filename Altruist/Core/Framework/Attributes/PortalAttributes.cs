namespace Altruist;


[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class PortalAttribute : Attribute
{
    public string Endpoint { get; }
    public string? Context { get; }

    public PortalAttribute(string endpoint, string? context = "")
    {
        Endpoint = endpoint;
        Context = context;
    }
}

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class GateAttribute : Attribute
{
    public string Event { get; }

    public GateAttribute(string eventName)
    {
        Event = eventName;
    }
}
