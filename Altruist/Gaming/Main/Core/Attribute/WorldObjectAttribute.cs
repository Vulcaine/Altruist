/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming
{
    /// <summary>
    /// Marks a world-entity type with a logical archetype name coming from the level data.
    /// The 2D/3D world loaders discover every class with this attribute (not inherited) that is a
    /// world object of their dimension and, when a level-data node carries an <c>archetype</c>
    /// (matched case-insensitively; the first type found wins on duplicates), instantiate that type
    /// for the node's body. The world managers also file spawned objects under their archetype,
    /// so archetype queries find them. Objects without the attribute have archetype <c>""</c>.
    /// <para>
    /// Use it for world objects that come from level data or that you query by kind; for match rooms
    /// (<c>RoomHost</c>) entities live in the game's own simulation and need no attribute.
    /// </para>
    /// <example>
    /// <code>
    /// [WorldObject("Tree")]
    /// public sealed class TreeObject : WorldObject3D { ... }
    /// </code>
    /// </example>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class WorldObjectAttribute : Attribute
    {
        /// <summary>The archetype name as written in the level data (for example <c>Tree</c>).</summary>
        public string Archetype { get; }

        /// <summary>Marks the class with an archetype name.</summary>
        /// <param name="archetype">The archetype name used by the level data.</param>
        /// <exception cref="ArgumentNullException"><paramref name="archetype"/> is null.</exception>
        public WorldObjectAttribute(string archetype)
        {
            Archetype = archetype ?? throw new ArgumentNullException(nameof(archetype));
        }
    }
}
