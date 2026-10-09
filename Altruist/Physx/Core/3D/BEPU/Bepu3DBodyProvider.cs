/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// BEPU implementation of <see cref="IPhysxBodyApiProvider3D"/>. Registered in DI as a singleton <see cref="IPhysxBodyApiProvider3D"/>
    /// when the config key <c>altruist:environment:mode</c> equals <c>3D</c>. Works only with <see cref="BepuWorldEngine3D"/>
    /// engines and the body adapters and colliders the BEPU providers create.
    /// </summary>
    /// <remarks>
    /// <see cref="CreateBody"/> inserts the body into the BEPU simulation immediately (under the engine lock) with a default
    /// box shape sized from the descriptor (<c>Transform.Size</c> = half extents), used until a collider is attached.
    /// Static descriptors become BEPU statics; kinematic ones (flag or type) get infinite mass and are not affected by
    /// gravity; dynamic ones use the descriptor mass (1 if non-positive). The descriptor's <c>PhysxTag</c> becomes the
    /// body's tag. Bodies use a 0.1 speculative margin and a 0.01 sleep threshold.
    /// </remarks>
    [Service(typeof(IPhysxBodyApiProvider3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public sealed class BepuPhysxBodyApiProvider3D : IPhysxBodyApiProvider3D
    {
        /// <summary>Creates the stateless provider.</summary>
        public BepuPhysxBodyApiProvider3D() { }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="engine"/> is not a BEPU engine, or it already has a body with the descriptor's id.
        /// </exception>
        public IPhysxBody3D CreateBody(IPhysxWorldEngine3D engine, in PhysxBody3DDesc desc)
            => Bepu(engine).CreateBody(desc);

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">The engine or body is not BEPU-backed, or the body belongs to another engine.</exception>
        public void AddCollider(IPhysxWorldEngine3D engine, IPhysxBody3D body, IPhysxCollider3D collider)
        {
            var bepu = Bepu(engine);

            if (body is not BepuWorldEngine3D.Body3DAdapterBase adapter || !ReferenceEquals(adapter.Engine, bepu))
                throw new InvalidOperationException("Body must be a BEPU body created for this engine.");

            adapter.AddCollider(collider);
        }

        /// <summary>
        /// Finds the body of <paramref name="engine"/> holding this exact collider instance and detaches it (the body's
        /// shape is rebuilt from its remaining colliders). No-op when no body of the engine holds it.
        /// </summary>
        /// <param name="engine">BEPU engine.</param>
        /// <param name="collider">Collider to detach.</param>
        /// <exception cref="InvalidOperationException"><paramref name="engine"/> is not a <see cref="BepuWorldEngine3D"/>.</exception>
        public void RemoveCollider(IPhysxWorldEngine3D engine, IPhysxCollider3D collider)
        {
            var owner = Bepu(engine).Bodies
                .OfType<BepuWorldEngine3D.Body3DAdapterBase>()
                .FirstOrDefault(b => b.TryGetColliderById(collider.Id, out var found) && ReferenceEquals(found, collider));

            owner?.RemoveCollider(collider);
        }

        private static BepuWorldEngine3D Bepu(IPhysxWorldEngine3D engine)
            => engine as BepuWorldEngine3D ?? throw new InvalidOperationException("Engine must be a BEPU-backed engine.");
    }
}
