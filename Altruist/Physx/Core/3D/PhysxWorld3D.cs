
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
}
