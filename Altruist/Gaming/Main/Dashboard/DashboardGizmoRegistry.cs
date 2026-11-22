namespace Altruist.Dashboard;

/// <summary>
/// Server-side store of debug gizmos (spheres, circles, rects, boxes, polylines, ...) drawn over a world in the dashboard
/// viewer. Game code registers gizmos by id; the dashboard pulls full snapshots over HTTP and incremental changes over the
/// dashboard websocket.
/// </summary>
/// <remarks>
/// <para>Inject <see cref="IDashboardGizmoRegistry"/> (singleton <see cref="DashboardGizmoRegistry"/>, thread-safe) from any
/// service. Gizmos are only displayed when the dashboard is enabled (<c>altruist:dashboard:enabled = true</c>), but the
/// registry itself is always registered, so calls are safe either way. Use it for debug visualisation only; it is not a
/// gameplay replication channel.</para>
/// <para>Gizmos are keyed by <see cref="DashboardGizmo.Id"/> (ordinal), belong to one <see cref="DashboardGizmo.WorldIndex"/>,
/// and expire automatically when <see cref="DashboardGizmo.TtlSeconds"/> &gt; 0 (counted from creation). Use a stable id per
/// visual and re-<see cref="Upsert"/> it to move it; use <see cref="ClearCategory"/> to wipe a whole layer.</para>
/// </remarks>
/// <example><code>
/// gizmos.Upsert(new DashboardGizmo
/// {
///     Id = $"spawn:{spawnId}",
///     WorldIndex = 0,
///     Category = "spawns",
///     Source = "my-game",
///     Type = "circle",
///     Color = "#F59E0BFF",
///     Position = new Vector3Dto { X = p.X, Y = p.Y, Z = p.Z },
///     Radius = 4f,
/// });
/// gizmos.Update($"spawn:{spawnId}", g =&gt; g.Color = "#EF4444FF");
/// gizmos.ClearCategory("spawns");
/// </code></example>
public interface IDashboardGizmoRegistry
{
    /// <summary>Alias of <see cref="Upsert"/>.</summary>
    /// <param name="gizmo">Gizmo to add or replace.</param>
    void Register(DashboardGizmo gizmo);
    /// <summary>
    /// Adds or replaces the gizmo with the same id (a copy is stored, so later changes to <paramref name="gizmo"/> have no
    /// effect). Sets <see cref="DashboardGizmo.UpdatedAtUtc"/> to now and <see cref="DashboardGizmo.CreatedAtUtc"/> to now
    /// unless provided; moving a gizmo to another world reports it removed from the old one.
    /// </summary>
    /// <param name="gizmo">Gizmo to store.</param>
    /// <exception cref="ArgumentException"><see cref="DashboardGizmo.Id"/> is null or whitespace.</exception>
    void Upsert(DashboardGizmo gizmo);
    /// <summary>Marks an existing gizmo as changed (re-sent to connected dashboards) and bumps its update time; does not extend its TTL.</summary>
    /// <param name="id">Gizmo id.</param>
    /// <returns>False if the id is blank or unknown.</returns>
    bool Update(string id);
    /// <summary>
    /// Edits an existing gizmo in place through a <see cref="DashboardGizmoBuilder"/> pre-filled with its current values,
    /// then marks it changed. The id cannot be changed (the builder's <see cref="DashboardGizmoBuilder.Id"/> is ignored);
    /// creation time and TTL origin are preserved.
    /// </summary>
    /// <param name="id">Gizmo id.</param>
    /// <param name="configure">Callback that mutates the builder; runs under the registry lock, so keep it short.</param>
    /// <returns>False if the id is blank or unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    bool Update(string id, Action<DashboardGizmoBuilder> configure);
    /// <summary>Removes a gizmo and reports its id as removed to connected dashboards.</summary>
    /// <param name="id">Gizmo id.</param>
    /// <returns>False if the id is blank or unknown.</returns>
    bool Remove(string id);
    /// <summary>Removes every gizmo whose <see cref="DashboardGizmo.Category"/> matches (case-insensitive).</summary>
    /// <param name="category">Category to clear; blank clears nothing.</param>
    /// <returns>Number of gizmos removed.</returns>
    int ClearCategory(string category);
    /// <summary>Returns copies of all live gizmos of a world, ordered by category then id (used by the HTTP snapshot). Does not consume pending changes.</summary>
    /// <param name="worldIndex">World index.</param>
    /// <returns>Gizmo copies; empty when none.</returns>
    DashboardGizmo[] GetSnapshot(int worldIndex);
    /// <summary>
    /// Returns and clears the gizmos changed and ids removed in a world since the previous drain (used by the dashboard
    /// portal to push deltas). Each change is delivered to exactly one drain call, so only one consumer should drain.
    /// </summary>
    /// <param name="worldIndex">World index.</param>
    /// <returns>Changed gizmo copies and removed ids.</returns>
    DashboardGizmoChangeSet DrainChanges(int worldIndex);
}

/// <summary>
/// Default thread-safe (single lock) <see cref="IDashboardGizmoRegistry"/>, registered as a DI singleton. Expired gizmos are
/// purged lazily on most calls; inject the interface rather than this type.
/// </summary>
[Service(typeof(IDashboardGizmoRegistry))]
public sealed class DashboardGizmoRegistry : IDashboardGizmoRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DashboardGizmo> _gizmos = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyIds = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> _removedByWorld = new();

    /// <inheritdoc/>
    public void Register(DashboardGizmo gizmo) => Upsert(gizmo);

    /// <inheritdoc/>
    public void Upsert(DashboardGizmo gizmo)
    {
        if (string.IsNullOrWhiteSpace(gizmo.Id))
            throw new ArgumentException("Dashboard gizmo id is required.", nameof(gizmo));

        lock (_gate)
        {
            PurgeExpiredCore(DateTime.UtcNow);
            var now = DateTime.UtcNow;
            var copy = Clone(gizmo);
            copy.CreatedAtUtc = copy.CreatedAtUtc == default ? now : copy.CreatedAtUtc;
            copy.UpdatedAtUtc = now;

            if (_gizmos.TryGetValue(copy.Id, out var previous) && previous.WorldIndex != copy.WorldIndex)
                MarkRemoved(previous.WorldIndex, copy.Id);

            _gizmos[copy.Id] = copy;
            _dirtyIds.Add(copy.Id);
        }
    }

    /// <inheritdoc/>
    public bool Update(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_gate)
        {
            PurgeExpiredCore(DateTime.UtcNow);
            if (!_gizmos.TryGetValue(id, out var gizmo))
                return false;

            gizmo.UpdatedAtUtc = DateTime.UtcNow;
            _dirtyIds.Add(id);
            return true;
        }
    }

    /// <inheritdoc/>
    public bool Update(string id, Action<DashboardGizmoBuilder> configure)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        ArgumentNullException.ThrowIfNull(configure);

        lock (_gate)
        {
            PurgeExpiredCore(DateTime.UtcNow);
            if (!_gizmos.TryGetValue(id, out var existing))
                return false;

            int previousWorld = existing.WorldIndex;
            var builder = new DashboardGizmoBuilder(existing);
            configure(builder);
            var updated = builder.Build(id);
            updated.CreatedAtUtc = existing.CreatedAtUtc == default ? DateTime.UtcNow : existing.CreatedAtUtc;
            updated.UpdatedAtUtc = DateTime.UtcNow;

            if (previousWorld != updated.WorldIndex)
                MarkRemoved(previousWorld, id);

            _gizmos[id] = updated;
            _dirtyIds.Add(id);
            return true;
        }
    }

    /// <inheritdoc/>
    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_gate)
        {
            if (!_gizmos.Remove(id, out var gizmo))
                return false;

            _dirtyIds.Remove(id);
            MarkRemoved(gizmo.WorldIndex, id);
            return true;
        }
    }

    /// <inheritdoc/>
    public int ClearCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return 0;

        lock (_gate)
        {
            var ids = _gizmos
                .Where(kv => string.Equals(kv.Value.Category, category, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToArray();

            foreach (var id in ids)
                RemoveCore(id);

            return ids.Length;
        }
    }

    /// <inheritdoc/>
    public DashboardGizmo[] GetSnapshot(int worldIndex)
    {
        lock (_gate)
        {
            PurgeExpiredCore(DateTime.UtcNow);
            return _gizmos.Values
                .Where(g => g.WorldIndex == worldIndex)
                .OrderBy(g => g.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Id, StringComparer.Ordinal)
                .Select(Clone)
                .ToArray();
        }
    }

    /// <inheritdoc/>
    public DashboardGizmoChangeSet DrainChanges(int worldIndex)
    {
        lock (_gate)
        {
            PurgeExpiredCore(DateTime.UtcNow);

            var gizmos = _dirtyIds
                .Where(id => _gizmos.TryGetValue(id, out var gizmo) && gizmo.WorldIndex == worldIndex)
                .ToArray();

            foreach (var id in gizmos)
                _dirtyIds.Remove(id);

            var removed = _removedByWorld.TryGetValue(worldIndex, out var removedIds)
                ? removedIds.ToArray()
                : Array.Empty<string>();

            if (removed.Length > 0)
                _removedByWorld.Remove(worldIndex);

            return new DashboardGizmoChangeSet
            {
                Gizmos = gizmos.Select(id => Clone(_gizmos[id])).ToArray(),
                RemovedGizmoIds = removed,
            };
        }
    }

    private void RemoveCore(string id)
    {
        if (!_gizmos.Remove(id, out var gizmo))
            return;

        _dirtyIds.Remove(id);
        MarkRemoved(gizmo.WorldIndex, id);
    }

    private void PurgeExpiredCore(DateTime now)
    {
        var expired = _gizmos
            .Where(kv => kv.Value.TtlSeconds > 0f
                && kv.Value.CreatedAtUtc != default
                && kv.Value.CreatedAtUtc.AddSeconds(kv.Value.TtlSeconds) <= now)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var id in expired)
            RemoveCore(id);
    }

    private void MarkRemoved(int worldIndex, string id)
    {
        if (!_removedByWorld.TryGetValue(worldIndex, out var removed))
        {
            removed = new HashSet<string>(StringComparer.Ordinal);
            _removedByWorld[worldIndex] = removed;
        }

        removed.Add(id);
    }

    internal static DashboardGizmo Clone(DashboardGizmo gizmo)
    {
        return new DashboardGizmo
        {
            Id = gizmo.Id,
            WorldIndex = gizmo.WorldIndex,
            Category = gizmo.Category,
            Source = gizmo.Source,
            Type = gizmo.Type,
            Label = gizmo.Label,
            Color = gizmo.Color,
            Position = new Vector3Dto
            {
                X = gizmo.Position?.X ?? 0f,
                Y = gizmo.Position?.Y ?? 0f,
                Z = gizmo.Position?.Z ?? 0f,
            },
            Radius = gizmo.Radius,
            Width = gizmo.Width,
            Height = gizmo.Height,
            Points = gizmo.Points
                .Select(p => new DashboardGizmoPoint { X = p.X, Y = p.Y, Z = p.Z })
                .ToList(),
            AttachToInstanceId = gizmo.AttachToInstanceId,
            TtlSeconds = gizmo.TtlSeconds,
            CreatedAtUtc = gizmo.CreatedAtUtc,
            UpdatedAtUtc = gizmo.UpdatedAtUtc,
        };
    }
}

/// <summary>
/// Mutable working copy of a <see cref="DashboardGizmo"/> passed to
/// <see cref="IDashboardGizmoRegistry.Update(string, System.Action{DashboardGizmoBuilder})"/>; set the properties you want to change.
/// See <see cref="DashboardGizmo"/> for the meaning of each field.
/// </summary>
public sealed class DashboardGizmoBuilder
{
    /// <summary>Current id (read-only in effect: <see cref="Build"/> uses the id passed to it).</summary>
    public string Id { get; set; }
    /// <summary>World index; changing it moves the gizmo to another world.</summary>
    public int WorldIndex { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Category"/>.</summary>
    public string Category { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Source"/>.</summary>
    public string Source { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Type"/>.</summary>
    public string Type { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Label"/>.</summary>
    public string Label { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Color"/>.</summary>
    public string Color { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Position"/>.</summary>
    public Vector3Dto Position { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Radius"/>.</summary>
    public float Radius { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Width"/>.</summary>
    public float Width { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Height"/>.</summary>
    public float Height { get; set; }
    /// <summary>See <see cref="DashboardGizmo.Points"/> (a private copy; safe to mutate).</summary>
    public List<DashboardGizmoPoint> Points { get; set; }
    /// <summary>See <see cref="DashboardGizmo.AttachToInstanceId"/>.</summary>
    public string AttachToInstanceId { get; set; }
    /// <summary>See <see cref="DashboardGizmo.TtlSeconds"/>; still measured from the original creation time.</summary>
    public float TtlSeconds { get; set; }
    /// <summary>Original creation time (overwritten by the registry on update).</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Last update time (overwritten by the registry on update).</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Creates a builder pre-filled with a deep copy of <paramref name="gizmo"/>.</summary>
    /// <param name="gizmo">Gizmo to copy.</param>
    public DashboardGizmoBuilder(DashboardGizmo gizmo)
    {
        var copy = DashboardGizmoRegistry.Clone(gizmo);
        Id = copy.Id;
        WorldIndex = copy.WorldIndex;
        Category = copy.Category;
        Source = copy.Source;
        Type = copy.Type;
        Label = copy.Label;
        Color = copy.Color;
        Position = copy.Position;
        Radius = copy.Radius;
        Width = copy.Width;
        Height = copy.Height;
        Points = copy.Points;
        AttachToInstanceId = copy.AttachToInstanceId;
        TtlSeconds = copy.TtlSeconds;
        CreatedAtUtc = copy.CreatedAtUtc;
        UpdatedAtUtc = copy.UpdatedAtUtc;
    }

    /// <summary>Creates a new <see cref="DashboardGizmo"/> from the builder's values.</summary>
    /// <param name="id">Id to give the gizmo (the builder's <see cref="Id"/> is ignored).</param>
    /// <returns>The built gizmo.</returns>
    public DashboardGizmo Build(string id)
    {
        return new DashboardGizmo
        {
            Id = id,
            WorldIndex = WorldIndex,
            Category = Category,
            Source = Source,
            Type = Type,
            Label = Label,
            Color = Color,
            Position = Position,
            Radius = Radius,
            Width = Width,
            Height = Height,
            Points = Points,
            AttachToInstanceId = AttachToInstanceId,
            TtlSeconds = TtlSeconds,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
        };
    }
}
