
using System.Numerics;

using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD;

/// <summary>
/// Default <see cref="IPhysxWorld3D"/>: a thin pass-through facade over one <see cref="IPhysxWorldEngine3D"/>.
/// Threading and stepping semantics are those of the wrapped engine.
/// </summary>
/// <example>
/// <code>
/// var world = new PhysxWorld3D(engineFactory.GetExistingOrCreate(new Vector3(0, -9.81f, 0)));
/// world.AddBody(body);
/// world.Step(deltaSeconds);                 // fixed-step accumulator inside the engine
/// var hit = world.RayCast(new PhysxRay3D(from, to)).FirstOrDefault();
/// </code>
/// </example>
public sealed class PhysxWorld3D : IPhysxWorld3D, IDisposable
{
    /// <summary>Bodies registered with the engine (see <see cref="IPhysxWorldEngine3D.Bodies"/>).</summary>
    public IReadOnlyCollection<IPhysxBody> Bodies => _engine.Bodies;

    private readonly IPhysxWorldEngine3D _engine;

    /// <inheritdoc/>
    public IPhysxWorldEngine3D Engine => _engine;

    /// <summary>Wraps <paramref name="engine"/>. The world takes ownership: <see cref="Dispose"/> disposes the engine.</summary>
    /// <param name="engine">Engine to wrap.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is <see langword="null"/>.</exception>
    public PhysxWorld3D(IPhysxWorldEngine3D engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>Advances the engine by <paramref name="deltaTime"/> seconds (see <see cref="IPhysxWorldEngine3D.Step"/>).</summary>
    /// <param name="deltaTime">Elapsed time in seconds.</param>
    public void Step(float deltaTime) => _engine.Step(deltaTime);

    /// <inheritdoc/>
    public void AddBody(IPhysxBody3D body) => _engine.AddBody(body);

    /// <summary>Removes a body (see <see cref="IPhysxWorldEngine3D.RemoveBody"/>; deferred to the next step in BEPU).</summary>
    /// <param name="body">Body to remove.</param>
    public void RemoveBody(IPhysxBody3D body) => _engine.RemoveBody(body);

    /// <inheritdoc/>
    public IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1)
        => _engine.RayCast(ray, maxHits);

    /// <summary>Disposes the wrapped engine (which may be shared through the factory cache).</summary>
    public void Dispose() => _engine.Dispose();

    /// <summary>
    /// Legacy nested copies of the engine contracts. Not used by <see cref="PhysxWorld3D"/> or the BEPU backend; use the
    /// top-level <see cref="Altruist.Physx.ThreeD.IPhysxWorldEngine3D"/> and
    /// <see cref="Altruist.Physx.ThreeD.IPhysxWorldEngineFactory3D"/> instead.
    /// </summary>
    public static class Contracts
    {
        /// <summary>Legacy engine contract; prefer the top-level <see cref="Altruist.Physx.ThreeD.IPhysxWorldEngine3D"/>.</summary>
        public interface IPhysxWorldEngine3D : IDisposable
        {
            /// <summary>Fixed simulation sub-step in seconds.</summary>
            float FixedDeltaTime { get; }
            /// <summary>Registered bodies.</summary>
            IReadOnlyCollection<IPhysxBody> Bodies { get; }
            /// <summary>Advances the simulation.</summary>
            /// <param name="deltaTime">Elapsed time in seconds.</param>
            void Step(float deltaTime);
            /// <summary>Registers a body and returns it.</summary>
            /// <param name="body">Body to add.</param>
            IPhysxBody3D AddBody(IPhysxBody3D body);
            /// <summary>Removes a body.</summary>
            /// <param name="body">Body to remove.</param>
            void RemoveBody(IPhysxBody body);
            /// <summary>Casts a ray segment.</summary>
            /// <param name="ray">Ray segment.</param>
            /// <param name="maxHits">Maximum hits.</param>
            IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1);
        }

        /// <summary>Legacy factory contract; prefer the top-level <see cref="Altruist.Physx.ThreeD.IPhysxWorldEngineFactory3D"/>.</summary>
        public interface IPhysxWorldEngineFactory3D
        {
            /// <summary>Creates an engine.</summary>
            /// <param name="gravity">Gravity acceleration (units/s²).</param>
            /// <param name="fixedDeltaTime">Fixed sub-step in seconds.</param>
            IPhysxWorldEngine3D Create(Vector3 gravity, float fixedDeltaTime = 1f / 60f);
        }
    }
}
