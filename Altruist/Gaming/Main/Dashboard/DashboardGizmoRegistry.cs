namespace Altruist.Dashboard;

public interface IDashboardGizmoRegistry
{
    void Register(DashboardGizmo gizmo);
    void Upsert(DashboardGizmo gizmo);
    bool Update(string id);
    bool Update(string id, Action<DashboardGizmoBuilder> configure);
    bool Remove(string id);
    int ClearCategory(string category);
    DashboardGizmo[] GetSnapshot(int worldIndex);
    DashboardGizmoChangeSet DrainChanges(int worldIndex);
}

[Service(typeof(IDashboardGizmoRegistry))]
public sealed class DashboardGizmoRegistry : IDashboardGizmoRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DashboardGizmo> _gizmos = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtyIds = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> _removedByWorld = new();

    public void Register(DashboardGizmo gizmo) => Upsert(gizmo);

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

public sealed class DashboardGizmoBuilder
{
    public string Id { get; set; }
    public int WorldIndex { get; set; }
    public string Category { get; set; }
    public string Source { get; set; }
    public string Type { get; set; }
    public string Label { get; set; }
    public string Color { get; set; }
    public Vector3Dto Position { get; set; }
    public float Radius { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public List<DashboardGizmoPoint> Points { get; set; }
    public string AttachToInstanceId { get; set; }
    public float TtlSeconds { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

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
