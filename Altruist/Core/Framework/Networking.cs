/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Buffers;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;

using Altruist.UORM;

namespace Altruist.Networking;

/// <summary>
/// Marks a public instance property of an <see cref="ISynchronizedEntity"/> as part of its network-synchronized
/// surface. Only properties carrying this attribute are diffed by <see cref="Synchronization.GetSyncChanges{TType}"/>
/// and sent in delta sync packets.
/// </summary>
/// <remarks>
/// Pair with <see cref="SynchronizedAttribute"/> on the class to have the framework sync the entity automatically.
/// Properties declared on base classes are enumerated first; a derived class's <see cref="BitIndex"/> values are
/// offset past the highest base-class index (see <see cref="SyncMetadataHelper.GetSyncMetadata"/>).
/// The change mask bit actually set for a property is its position in that ordered list, not the raw
/// <see cref="BitIndex"/> value, so keep indices dense and in declaration order.
/// </remarks>
/// <example><code>
/// [Synchronized]
/// public class PlayerState : ISynchronizedEntity
/// {
///     public string ClientId { get; set; } = "";
///
///     [Synced(0)] public float X { get; set; }
///     [Synced(1)] public float Y { get; set; }
///     [Synced(2, SyncAlways: true)] public string VirtualId { get; set; } = "";
/// }
/// </code></example>
[AttributeUsage(AttributeTargets.Property)]
public class SyncedAttribute : Attribute
{
    /// <summary>Declared bit index of this property within its declaring class (0-based, local to the class).</summary>
    public int BitIndex { get; set; }
    /// <summary>
    /// When true the property is included in every sync packet that is sent for the entity (it piggy-backs on real
    /// deltas; it never forces a packet on its own). Use for routing/identity fields the client needs to demux deltas.
    /// </summary>
    public bool? SyncAlways { get; }

    /// <summary>
    /// When true the property is also sent while its last-sent value is still <c>null</c> (i.e. until a non-null value
    /// has been delivered), in addition to normal change detection.
    /// </summary>
    public bool oneTime { get; }

    /// <summary>
    /// Fixed send interval in engine ticks. 0 (default) sends only on change. When &gt; 0 the property is sent on every
    /// tick where <c>tick % syncFrequency == 0</c>, whether or not it changed, and is not sent on other ticks even if it changed.
    /// </summary>
    public uint syncFrequency { get; }

    /// <summary>Marks the property as synchronized.</summary>
    /// <param name="BitIndex">Bit index local to the declaring class (0-based).</param>
    /// <param name="syncFrequency">Fixed send interval in engine ticks; 0 means "send on change".</param>
    /// <param name="oneTime">Also send while the last-sent value is <c>null</c>.</param>
    /// <param name="SyncAlways">Include in every packet that carries a real delta for the entity.</param>
    public SyncedAttribute(int BitIndex, uint syncFrequency = 0, bool oneTime = false, bool SyncAlways = false)
    {
        this.BitIndex = BitIndex;
        this.SyncAlways = SyncAlways;
        this.syncFrequency = syncFrequency;
        this.oneTime = oneTime;
    }
}

/// <summary>
/// An object whose <see cref="SyncedAttribute"/> properties can be delta-synchronized to clients.
/// Implement it on world objects / models and add <see cref="SynchronizedAttribute"/> for automatic sync,
/// or call a router/synchronizer's send method manually.
/// </summary>
public interface ISynchronizedEntity
{
    /// <summary>
    /// Connection id of the owning client (receives self-sync). Leave empty for server-owned entities (AI/NPC),
    /// which only reach observers.
    /// </summary>
    public string ClientId { get; set; }
}

/// <summary>
/// Marks an ISynchronizedEntity class for automatic delta synchronization.
/// Altruist discovers all world objects with this attribute and broadcasts
/// [Synced] property changes automatically — no manual code needed.
///
/// Default: syncs every world step. A frequency is read in <see cref="SyncUnit.Ticks"/> unless a unit is given.
///
/// Usage:
///   [Synchronized]                     // sync every step
///   [Synchronized(10)]                 // sync every 10th step
///   [Synchronized(10, SyncUnit.Hz)]    // sync 10 times per second
///   [Synchronized(1, SyncUnit.Seconds)] // sync once per second
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class SynchronizedAttribute : Attribute
{
    /// <summary>Sync frequency value interpreted by <see cref="Unit"/>. 0 means every engine tick.</summary>
    public int Frequency { get; }
    /// <summary>Unit in which <see cref="Frequency"/> is expressed.</summary>
    public SyncUnit Unit { get; }

    /// <summary>Sync at engine tick rate.</summary>
    public SynchronizedAttribute()
    {
        Frequency = 0; // 0 = engine tick rate
        Unit = SyncUnit.Ticks;
    }

    /// <summary>Sync at a custom frequency; without <paramref name="unit"/> it is a step count
    /// (<c>[Synchronized(10)]</c> syncs every 10th step, not 10 times per second).</summary>
    public SynchronizedAttribute(int frequency, SyncUnit unit = SyncUnit.Ticks)
    {
        Frequency = frequency;
        Unit = unit;
    }
}

/// <summary>Unit for <see cref="SynchronizedAttribute.Frequency"/>.</summary>
public enum SyncUnit
{
    /// <summary>Sync every N engine ticks.</summary>
    Ticks,   // per N engine ticks
    /// <summary>Sync N times per second (interval = engine Hz / N ticks).</summary>
    Hz,      // N times per second
    /// <summary>Sync once every N seconds (interval = engine Hz * N ticks).</summary>
    Seconds, // every N seconds
}

/// <summary>
/// Cached reflection metadata for one <see cref="SyncedAttribute"/> property: its resolved bit index, sync flags
/// and a compiled getter. Produced by <see cref="SyncMetadataHelper.GetSyncMetadata"/>; you rarely construct it yourself.
/// </summary>
public sealed class SyncedProperty
{
    /// <summary>Property name; also the key used in the changed-data dictionary.</summary>
    public string Name { get; }
    /// <summary>Resolved (inheritance-adjusted) bit index.</summary>
    public int BitIndex { get; set; }
    /// <summary>See <see cref="SyncedAttribute.SyncAlways"/>.</summary>
    public bool SyncAlways { get; }
    /// <summary>See <see cref="SyncedAttribute.oneTime"/>.</summary>
    public bool OneTime { get; }
    /// <summary>See <see cref="SyncedAttribute.syncFrequency"/> (ticks; 0 = on change).</summary>
    public uint SyncTickFrequency { get; }
    /// <summary>Compiled accessor returning the boxed property value for an entity instance.</summary>
    public Func<object, object?> Getter { get; }

    /// <summary>Creates property metadata.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="bitIndex">Resolved bit index.</param>
    /// <param name="syncAlways">Include in every delta packet.</param>
    /// <param name="oneTime">Send while last-sent value is null.</param>
    /// <param name="syncFrequency">Fixed send interval in ticks (0 = on change).</param>
    /// <param name="getter">Compiled getter.</param>
    public SyncedProperty(string name, int bitIndex, bool syncAlways, bool oneTime, uint syncFrequency, Func<object, object?> getter)
    {
        Name = name;
        BitIndex = bitIndex;
        SyncAlways = syncAlways;
        Getter = getter;
        OneTime = oneTime;
        SyncTickFrequency = syncFrequency;
    }
}

/// <summary>
/// Zero-allocation change map returned by Synchronization.GetChangedData.
/// Wraps the pooled per-entity dictionary — caller must NOT hold a reference after the next GetChangedData call.
/// The masks array is rented from ArrayPool and must be returned by the caller.
/// </summary>
public struct SyncChangeMap : IDisposable
{
    /// <summary>Change bitmask words (64 properties per word), rented from <see cref="ArrayPool{T}.Shared"/>; may be longer than <see cref="MaskCount"/>.</summary>
    public ulong[] Masks;
    /// <summary>Number of meaningful words in <see cref="Masks"/>.</summary>
    public int MaskCount;
    /// <summary>Changed property values keyed by property name. Pooled per entity: reused by the next call for the same key.</summary>
    public Dictionary<string, object?> Data;
    /// <summary>True when at least one bit is set in <see cref="Masks"/>.</summary>
    public bool HasChanges;

    /// <summary>Return the rented masks array to the pool. Call when done with the change map.</summary>
    public void Dispose()
    {
        if (Masks != null)
        {
            ArrayPool<ulong>.Shared.Return(Masks);
            Masks = null!;
        }
    }
}

/// <summary>
/// Delta-detection engine for <see cref="ISynchronizedEntity"/> objects: compares the current <see cref="SyncedAttribute"/>
/// property values with the last values recorded for a key and reports what changed.
/// </summary>
/// <remarks>
/// State is static and keyed by the string you pass as <c>clientId</c> (use a stable per-entity id such as an instance id;
/// entities sharing a key corrupt each other's diffs). Per-key access is locked, so different keys may be processed
/// concurrently. Recorded state is never evicted automatically.
/// </remarks>
public static class Synchronization
{
    private static readonly Dictionary<string, object?[]> _lastSyncedStates = new();
    private static readonly Dictionary<string, Dictionary<string, object?>> _lastSyncedData = new();
    private static readonly ConcurrentDictionary<string, object> _entityLocks = new();

    /// <summary>
    /// Detect changed [Synced] properties since last call for this entity.
    /// Returns a SyncChangeMap with zero new allocation (dictionary + masks reused per entity).
    /// Caller must call Dispose() on the returned map, or use 'using'.
    /// </summary>
    public static SyncChangeMap GetSyncChanges<TType>(
        TType newEntity,
        string clientId,
        long currentTick,
        bool forceAllAsChanged = false
    ) where TType : ISynchronizedEntity
    {
        var (masks, maskCount, data) = GetChangedData(newEntity, clientId, currentTick, forceAllAsChanged);

        bool hasChanges = false;
        for (int i = 0; i < maskCount; i++)
        {
            if (masks[i] != 0) { hasChanges = true; break; }
        }

        return new SyncChangeMap
        {
            Masks = masks,
            MaskCount = maskCount,
            Data = data,
            HasChanges = hasChanges,
        };
    }

    /// <summary>
    /// Low-level form of <see cref="GetSyncChanges{TType}"/>: returns the raw mask array, mask word count and the pooled
    /// changed-data dictionary, and records the sent values as the new baseline.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="GetSyncChanges{TType}"/>, which wraps the result in a disposable <see cref="SyncChangeMap"/>.
    /// If you call this directly you must return <c>Masks</c> to <see cref="ArrayPool{T}.Shared"/> yourself, and must not keep the
    /// dictionary past the next call for the same key.
    /// </remarks>
    /// <typeparam name="TType">Entity type.</typeparam>
    /// <param name="newEntity">Entity whose current values are compared.</param>
    /// <param name="clientId">Delta-state key (a stable per-entity id, despite the name).</param>
    /// <param name="currentTick">Current engine tick, used for <see cref="SyncedAttribute.syncFrequency"/> intervals.</param>
    /// <param name="forceAllAsChanged">Send every synced property now (e.g. full snapshot for a newly joined observer).</param>
    /// <returns>Mask words, number of meaningful words, and changed values by property name.</returns>
    public static (ulong[] Masks, int MaskCount, Dictionary<string, object?> ChangedData) GetChangedData<TType>(
        TType newEntity,
        string clientId,
        long currentTick,
        bool forceAllAsChanged = false
    ) where TType : ISynchronizedEntity
    {
        var entityLock = _entityLocks.GetOrAdd(clientId, static _ => new object());

        lock (entityLock)
        {
            // Networking must always enumerate the [Synced] surface of the entity.
            // `forceAllAsChanged` means "send every synced property right now",
            // not "switch to a different metadata source".
            var metadata = SyncMetadataHelper.GetSyncMetadata(
                newEntity.GetType(),
                onlySyncedProperties: true);
            var properties = metadata.Properties;
            var count = metadata.Count;

            int maskCount = (count + 63) / 64;
            var masks = ArrayPool<ulong>.Shared.Rent(maskCount);
            Array.Clear(masks, 0, maskCount);

            if (!_lastSyncedStates.TryGetValue(clientId, out var lastState))
            {
                lastState = new object[count];
                _lastSyncedStates[clientId] = lastState;
            }

            if (!_lastSyncedData.TryGetValue(clientId, out var changedData))
            {
                // Pre-size to property count — avoids resize allocations on subsequent .Clear() + re-add
                changedData = new Dictionary<string, object?>(count);
                _lastSyncedData[clientId] = changedData;
            }
            else
            {
                changedData.Clear();
            }

            bool anyChange = false;

            for (int i = 0; i < count; i++)
            {
                var prop = properties[i];
                if (prop.SyncAlways) continue; // handled below

                var newValue = prop.Getter(newEntity);
                var lastValue = lastState[i];

                bool shouldSync = forceAllAsChanged
                    || !AreValuesEqual(newValue, lastValue)
                    || (prop.OneTime && lastValue is null);
                shouldSync = forceAllAsChanged || shouldSync && prop.SyncTickFrequency == 0 || prop.SyncTickFrequency > 0 && currentTick % prop.SyncTickFrequency == 0;

                if (shouldSync)
                {
                    int maskIndex = i / 64;
                    int bitIndex = i % 64;
                    masks[maskIndex] |= 1UL << bitIndex;

                    changedData[prop.Name] = newValue;
                    lastState[i] = CloneValueIfNeeded(newValue);
                    anyChange = true;
                }
            }

            // SyncAlways means "include in any sync packet", not "force a packet
            // every tick". Sending one per visible entity per tick scales to
            // 1000s of entities × 25 Hz = catastrophic packet rate. Only piggy-
            // back the SyncAlways properties on packets that already carry a
            // real delta — routing fields like VirtualId still ride along
            // whenever something else changes, which is when the client
            // actually needs to demux them.
            if (anyChange || forceAllAsChanged)
            {
                var alwaysIndices = metadata.SyncAlwaysIndices;
                for (int j = 0; j < alwaysIndices.Length; j++)
                {
                    var i = alwaysIndices[j];
                    var prop = properties[i];
                    var newValue = prop.Getter(newEntity);

                    int maskIndex = i / 64;
                    int bitIndex = i % 64;
                    masks[maskIndex] |= 1UL << bitIndex;

                    changedData[prop.Name] = newValue;
                    lastState[i] = CloneValueIfNeeded(newValue);
                }
            }

            return (masks, maskCount, changedData);
        }
    }

    private static object? CloneValueIfNeeded(object? value)
    {
        if (value is null)
            return null;

        if (value is Array array)
            return array.Clone();

        if (value is string or ValueType)
            return value;

        return value;
    }

    private static bool AreValuesEqual(object? a, object? b)
    {
        if (a == null && b == null)
            return true;
        if (a == null || b == null)
            return false;

        if (a is Vector2 va && b is Vector2 vb)
            return va.Equals(vb);

        if (a is Array arrayA && b is Array arrayB)
        {
            if (arrayA.Length != arrayB.Length)
                return false;

            for (int i = 0; i < arrayA.Length; i++)
            {
                if (!AreValuesEqual(arrayA.GetValue(i), arrayB.GetValue(i)))
                    return false;
            }

            return true;
        }

        return a.Equals(b);
    }
}


/// <summary>Ordered list of <see cref="SyncedProperty"/> entries for one entity type, cached by <see cref="SyncMetadataHelper"/>.</summary>
public sealed class SyncMetadata
{
    /// <summary>Synced properties, base-class properties first; list position equals the change-mask bit.</summary>
    public List<SyncedProperty> Properties { get; }
    /// <summary>Number of synced properties.</summary>
    public int Count { get; }
    /// <summary>Indices into <see cref="Properties"/> of properties flagged <see cref="SyncedProperty.SyncAlways"/>.</summary>
    public int[] SyncAlwaysIndices { get; }

    /// <summary>Builds metadata and precomputes <see cref="SyncAlwaysIndices"/>.</summary>
    /// <param name="properties">Ordered synced properties.</param>
    public SyncMetadata(List<SyncedProperty> properties)
    {
        Properties = properties;
        Count = properties.Count;

        var alwaysIndices = new List<int>();
        for (int i = 0; i < Count; i++)
        {
            if (properties[i].SyncAlways)
                alwaysIndices.Add(i);
        }
        SyncAlwaysIndices = alwaysIndices.ToArray();
    }
}

/// <summary>Builds and caches <see cref="SyncMetadata"/> per entity type via reflection and compiled getters.</summary>
public static class SyncMetadataHelper
{
    private static readonly ConcurrentDictionary<Type, SyncMetadata> _syncMetadata = new();

    /// <summary>
    /// Returns the cached synced-property metadata for <paramref name="type"/>, walking base classes first and offsetting
    /// derived-class bit indices past the highest base-class index.
    /// </summary>
    /// <param name="type">Entity runtime type.</param>
    /// <param name="onlySyncedProperties">True to enumerate <see cref="SyncedAttribute"/> properties; false selects
    /// <see cref="VaultColumnAttribute"/> properties (only those that also carry <see cref="SyncedAttribute"/> end up in the list).</param>
    /// <returns>Cached metadata. Note: the cache is keyed by type only, so the first <paramref name="onlySyncedProperties"/> value used for a type wins.</returns>
    public static SyncMetadata GetSyncMetadata(Type type, bool onlySyncedProperties = true)
    {
        return _syncMetadata.GetOrAdd(type, t =>
        {
            var syncedProperties = new List<SyncedProperty>();
            int baseMaxBitIndex = -1;

            // Traverse inheritance tree (base classes first)
            var currentType = t.BaseType;
            while (currentType != null && currentType != typeof(object))
            {
                var baseProps = currentType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => ShouldIncludeProperty(p, onlySyncedProperties));

                foreach (var prop in baseProps)
                {
                    var attr = prop.GetCustomAttribute<SyncedAttribute>();

                    if (attr is null)
                        continue;

                    var bitIndex = attr.BitIndex;
                    var syncAlways = attr.SyncAlways;

                    if (bitIndex > baseMaxBitIndex)
                        baseMaxBitIndex = bitIndex;

                    syncedProperties.Add(BuildSyncedProperty(prop, bitIndex, syncAlways ?? false, attr.oneTime, attr.syncFrequency));
                }

                currentType = currentType.BaseType;
            }

            // Add local properties
            var localProps = t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(p => ShouldIncludeProperty(p, onlySyncedProperties));

            foreach (var prop in localProps)
            {
                var attr = prop.GetCustomAttribute<SyncedAttribute>();
                if (attr is null)
                    continue;

                var localBitIndex = attr.BitIndex;
                var globalBitIndex = baseMaxBitIndex + 1 + localBitIndex;

                syncedProperties.Add(BuildSyncedProperty(prop, globalBitIndex, attr.SyncAlways ?? false, attr.oneTime, attr.syncFrequency));
            }

            return new SyncMetadata(syncedProperties);
        });
    }

    private static SyncedProperty BuildSyncedProperty(PropertyInfo prop, int bitIndex, bool syncAlways, bool oneTime, uint syncFrequency)
    {
        var param = Expression.Parameter(typeof(object), "obj");
        var casted = Expression.Convert(param, prop.DeclaringType!);
        var access = Expression.Property(casted, prop);
        var convert = Expression.Convert(access, typeof(object));
        var lambda = Expression.Lambda<Func<object, object?>>(convert, param).Compile();

        return new SyncedProperty(prop.Name, bitIndex, syncAlways, oneTime, syncFrequency, lambda);
    }


    /// <summary>
    /// Determines if a property should be included based on its attributes.
    /// 
    /// - If <paramref name="onlySyncedProperties"/> is true:
    ///     - Only properties marked with <see cref="SyncedAttribute"/> are included.
    /// 
    /// - If <paramref name="onlySyncedProperties"/> is false:
    ///     - Only properties marked with <see cref="VaultColumnAttribute"/> are included.
    ///
    /// Notes:
    /// - Currently, it is assumed that all properties marked with <see cref="SyncedAttribute"/> 
    ///   are also persisted (i.e., have <see cref="VaultColumnAttribute"/>).
    /// - In the future, syncing (network) and persisting (vault) concerns might be separated.
    /// </summary>
    private static bool ShouldIncludeProperty(PropertyInfo prop, bool onlySyncedProperties)
    {
        return onlySyncedProperties
            ? Attribute.IsDefined(prop, typeof(SyncedAttribute))
            : Attribute.IsDefined(prop, typeof(VaultColumnAttribute));
    }
}
