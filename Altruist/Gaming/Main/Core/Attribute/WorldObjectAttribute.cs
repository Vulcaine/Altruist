/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming
{
    /// <summary>
    /// Marks a world-entity type with a logical archetype name coming from the level data.
    /// Example:
    ///   [WorldObject("Tree")]
    ///   public sealed class TreeObject : WorldObject3D { ... }
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class WorldObjectAttribute : Attribute
    {
        public string Archetype { get; }

        public WorldObjectAttribute(string archetype)
        {
            Archetype = archetype ?? throw new ArgumentNullException(nameof(archetype));
        }
    }
}
