namespace Altruist;

// ───────────────────────────────────────────────────────────────────────────
// Optional sugar
// ───────────────────────────────────────────────────────────────────────────
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PrefabAttribute : Attribute
{
    public string Id { get; }
    public PrefabAttribute(string id) => Id = id;
}

