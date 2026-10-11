/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Altruist.Dashboard
{
    /// <summary>
    /// What changed in one world since the previous delta; or, with <paramref name="Reset"/>, the whole world (every
    /// object in <paramref name="Added"/>, every gizmo in <paramref name="Gizmos"/>) that replaces what the viewer had.
    /// </summary>
    /// <param name="Reset">The viewer drops its picture and starts from this one.</param>
    /// <param name="WorldIndex">World index.</param>
    /// <param name="TimestampUtc">When the delta was taken.</param>
    /// <param name="Added">Objects that appeared, in full.</param>
    /// <param name="Moved">Objects whose pose, name or archetype changed.</param>
    /// <param name="Removed">Ids of objects that are gone.</param>
    /// <param name="Gizmos">Gizmos added or changed.</param>
    /// <param name="RemovedGizmoIds">Ids of gizmos that are gone.</param>
    public sealed record DashboardWorldDelta(
        bool Reset,
        int WorldIndex,
        DateTime TimestampUtc,
        IReadOnlyList<WorldObjectDto> Added,
        IReadOnlyList<DashboardObjectPoseDto> Moved,
        IReadOnlyList<string> Removed,
        IReadOnlyList<DashboardGizmo> Gizmos,
        IReadOnlyList<string> RemovedGizmoIds)
    {
        /// <summary>Whether nothing changed.</summary>
        public bool IsEmpty => !Reset && Added.Count == 0 && Moved.Count == 0 && Removed.Count == 0 && Gizmos.Count == 0 && RemovedGizmoIds.Count == 0;
    }

    /// <summary>One viewer of one world's deltas. Dispose it when the viewer leaves.</summary>
    public sealed class DashboardWorldSubscription : IDisposable
    {
        private readonly Action<DashboardWorldSubscription> _leave;
        // Wait mode: TryWrite then fails on a full queue (DropWrite would drop silently and report success).
        private readonly Channel<DashboardWorldDelta> _channel =
            Channel.CreateBounded<DashboardWorldDelta>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private int _needsBaseline = 1;

        internal DashboardWorldSubscription(int worldIndex, Action<DashboardWorldSubscription> leave)
        {
            WorldIndex = worldIndex;
            _leave = leave;
        }

        /// <summary>The world watched.</summary>
        public int WorldIndex { get; }

        /// <summary>The deltas, in order.</summary>
        public ChannelReader<DashboardWorldDelta> Deltas => _channel.Reader;

        // A new viewer, or one that read too slowly and lost deltas, gets the whole world next.
        internal bool NeedsBaseline => Volatile.Read(ref _needsBaseline) == 1;

        internal void Publish(DashboardWorldDelta delta)
        {
            if (delta.Reset)
                Volatile.Write(ref _needsBaseline, 0);
            if (!_channel.Writer.TryWrite(delta))
                Volatile.Write(ref _needsBaseline, 1);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _channel.Writer.TryComplete();
            _leave(this);
        }
    }

    /// <summary>
    /// Live changes of the worlds for the dashboard viewers (<c>GET /dashboard/v1/worlds/{index}/live</c>). Four times a
    /// second, on the engine thread, it compares every watched world with the previous look and hands each viewer what
    /// changed; a new viewer first gets the whole world, taken on the same thread, so its picture and the deltas fit.
    /// Nothing is compared while nobody watches; gizmo changes are always drained (so they never pile up) and passed on
    /// only when someone watches.
    /// </summary>
    [Service]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class DashboardWorldFeed
    {
        private readonly IDashboardWorldSource? _source;
        private readonly IDashboardGizmoRegistry _gizmos;
        private readonly ConcurrentDictionary<DashboardWorldSubscription, byte> _subscriptions = new();
        // Engine-thread state: what each watched world looked like at the last delta.
        private readonly Dictionary<int, Dictionary<string, DashboardObjectPoseDto>> _seen = new();
        private readonly Dictionary<int, HashSet<string>> _seenSourceGizmos = new();

        /// <summary>Created by DI.</summary>
        public DashboardWorldFeed(IDashboardGizmoRegistry gizmos, IDashboardWorldSource? source = null)
        {
            _source = source;
            _gizmos = gizmos;
        }

        /// <summary>Starts watching a world: the whole world at the next look, then what changes.</summary>
        public DashboardWorldSubscription Subscribe(int worldIndex)
        {
            var subscription = new DashboardWorldSubscription(worldIndex, s => _subscriptions.TryRemove(s, out _));
            _subscriptions[subscription] = 0;
            return subscription;
        }

        /// <summary>The number of viewers.</summary>
        public int ViewerCount => _subscriptions.Count;

        /// <summary>Takes the deltas and hands them out (the engine calls it).</summary>
        [Cycle(4, CycleUnit.Hz)]
        public void Publish()
        {
            if (_source is null)
                return;
            var watched = _subscriptions.Keys.Select(s => s.WorldIndex).ToHashSet();
            foreach (var world in _source.Worlds())
            {
                var changes = _gizmos.DrainChanges(world.Index);
                if (!watched.Contains(world.Index))
                {
                    // Nobody watches: the next viewer starts from a snapshot, so the old look is useless.
                    _seen.Remove(world.Index);
                    _seenSourceGizmos.Remove(world.Index);
                    continue;
                }

                var delta = Take(world.Index, changes);
                DashboardWorldDelta? baseline = null;
                foreach (var subscription in _subscriptions.Keys)
                {
                    if (subscription.WorldIndex != world.Index)
                        continue;
                    if (subscription.NeedsBaseline)
                        subscription.Publish(baseline ??= Baseline(world.Index));
                    else if (!delta.IsEmpty)
                        subscription.Publish(delta);
                }
            }
        }

        private DashboardWorldDelta Take(int worldIndex, DashboardGizmoChangeSet changes)
        {
            var first = !_seen.TryGetValue(worldIndex, out var seen);
            seen ??= _seen[worldIndex] = new Dictionary<string, DashboardObjectPoseDto>(StringComparer.Ordinal);

            var added = new List<WorldObjectDto>();
            var moved = new List<DashboardObjectPoseDto>();
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pose in _source!.LiveObjects(worldIndex))
            {
                present.Add(pose.InstanceId);
                if (seen.TryGetValue(pose.InstanceId, out var last))
                {
                    if (!last.SameAs(pose))
                        moved.Add(pose);
                }
                else if (!first && _source!.Object(worldIndex, pose.InstanceId) is { } full)
                {
                    added.Add(full);
                }
                seen[pose.InstanceId] = pose;
            }
            var removed = seen.Keys.Where(id => !present.Contains(id)).ToList();
            foreach (var id in removed)
                seen.Remove(id);

            var sourceGizmos = _source!.SourceGizmos(worldIndex).ToList();
            var sourceIds = sourceGizmos.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            var removedSourceGizmos = _seenSourceGizmos.TryGetValue(worldIndex, out var lastIds) ? lastIds.Where(id => !sourceIds.Contains(id)).ToList() : new List<string>();
            _seenSourceGizmos[worldIndex] = sourceIds;

            // The first look only records the world: every viewer starts from a baseline.
            if (first)
                return new DashboardWorldDelta(false, worldIndex, DateTime.UtcNow, [], [], [], [], []);

            // Visibility radii follow their observer: send them only when the observer moved or appeared.
            var movedIds = moved.Select(m => m.InstanceId).Concat(added.Select(a => a.InstanceId)).ToHashSet(StringComparer.Ordinal);
            var changedSourceGizmos = sourceGizmos.Where(g => movedIds.Contains(g.AttachToInstanceId) || lastIds is null || !lastIds.Contains(g.Id));

            return new DashboardWorldDelta(
                false,
                worldIndex,
                DateTime.UtcNow,
                added,
                moved,
                removed,
                changes.Gizmos.Concat(changedSourceGizmos).ToList(),
                changes.RemovedGizmoIds.Concat(removedSourceGizmos).Distinct(StringComparer.Ordinal).ToList());
        }

        // The whole world now, after Take recorded it, so the deltas that follow continue from exactly this.
        private DashboardWorldDelta Baseline(int worldIndex) => new(
            true,
            worldIndex,
            DateTime.UtcNow,
            _source!.Objects(worldIndex).ToList(),
            [],
            [],
            _gizmos.GetSnapshot(worldIndex).Concat(_source!.SourceGizmos(worldIndex)).ToList(),
            []);
    }
}
