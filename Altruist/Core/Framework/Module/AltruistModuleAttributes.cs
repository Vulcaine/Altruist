// Altruist/Modules/AltruistModuleAttributes.cs
namespace Altruist;

/// <summary>
/// Marks a type as an Altruist module. Must be applied to a public static class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AltruistModuleAttribute : Attribute
{
    public string? Name { get; }
    public AltruistModuleAttribute(string? name = null) => Name = name;
}

/// <summary>
/// Marks a method as a module loader entrypoint. Must be a public static void with zero parameters.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AltruistModuleLoaderAttribute : Attribute
{
}
