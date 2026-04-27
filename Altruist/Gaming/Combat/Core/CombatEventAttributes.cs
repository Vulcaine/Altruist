namespace Altruist.Gaming.Combat;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CombatHandlerAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CombatEventAttribute : Attribute
{
    public Type EventType { get; }

    public CombatEventAttribute(Type eventType)
    {
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
    }
}

public interface ICombatEventPayload
{
}
