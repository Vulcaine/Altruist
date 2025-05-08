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

[AttributeUsage(AttributeTargets.Property)]
public class SyncedAttribute : Attribute
{
    public int BitIndex { get; set; }
    public bool? SyncAlways { get; }
    public SyncedAttribute(int BitIndex, bool SyncAlways = false)
    {
        this.BitIndex = BitIndex;
        this.SyncAlways = SyncAlways;
    }
}

public interface ISynchronizedEntity
{
    public string ConnectionId { get; set; }
}

public sealed class SyncedProperty
{
    public string Name { get; }
    public int BitIndex { get; set; }
    public bool SyncAlways { get; }
    public Func<object, object?> Getter { get; }

    public SyncedProperty(string name, int bitIndex, bool syncAlways, Func<object, object?> getter)
    {
        Name = name;
        BitIndex = bitIndex;
        SyncAlways = syncAlways;
        Getter = getter;
    }
}

public static class Synchronization
{
    private static readonly ConcurrentDictionary<string, object?[]> _lastSyncedStates = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object?>> _lastSyncedData = new();
    private static readonly object _syncLock = new();

    public static (ulong[], ConcurrentDictionary<string, object?>) GetChangedData<TType>(
        TType newEntity,
        string clientId,
        bool forceAllAsChanged = false
    ) where TType : ISynchronizedEntity
    {
        lock (_syncLock)
        {
            var (properties, count) = SyncMetadataHelper.GetSyncMetadata(newEntity.GetType(), onlySyncedProperties: !forceAllAsChanged);

            int maskCount = (count + 63) / 64;
            var masks = ArrayPool<ulong>.Shared.Rent(maskCount);
            Array.Clear(masks, 0, maskCount);

            var lastState = _lastSyncedStates.GetOrAdd(clientId, _ => new object[count]);
            var changedData = _lastSyncedData.GetOrAdd(clientId, _ => new ConcurrentDictionary<string, object?>());
            changedData.Clear();

            List<int>? syncAlwaysIndices = null;
            bool nonSyncAlwaysChanged = false;

            for (int i = 0; i < count; i++)
            {
                var prop = properties[i];
                var newValue = prop.Getter(newEntity);
                var lastValue = lastState[i];

                bool shouldSync = forceAllAsChanged || !AreValuesEqual(newValue, lastValue);

                if (prop.SyncAlways)
                {
                    syncAlwaysIndices ??= new List<int>();
                    syncAlwaysIndices.Add(i);
                }

                if (shouldSync)
                {
                    nonSyncAlwaysChanged = true;

                    int maskIndex = i / 64;
                    int bitIndex = i % 64;
                    masks[maskIndex] |= 1UL << bitIndex;

                    changedData[prop.Name] = newValue;
                    lastState[i] = CloneValueIfNeeded(newValue);
                }
            }

            if (nonSyncAlwaysChanged && syncAlwaysIndices is not null)
            {
                foreach (var i in syncAlwaysIndices)
                {
                    var prop = properties[i];
                    var newValue = prop.Getter(newEntity);

                    int maskIndex = i / 64;
                    int bitIndex = i % 64;
                    masks[maskIndex] |= 1UL << bitIndex;

                    changedData[prop.Name] = newValue;
                    lastState[i] = CloneValueIfNeeded(newValue);
                }
            }

            return (masks, changedData);
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
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        if (a is Vector2 va && b is Vector2 vb)
            return va.Equals(vb);

        if (a is Array arrayA && b is Array arrayB)
        {
            if (arrayA.Length != arrayB.Length) return false;

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

public static class SyncMetadataHelper
{
    private static readonly ConcurrentDictionary<Type, (List<SyncedProperty>, int)> _syncMetadata = new();

    public static (List<SyncedProperty> Properties, int Count) GetSyncMetadata(Type type, bool onlySyncedProperties = true)
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
                    var bitIndex = attr?.BitIndex ?? throw new InvalidOperationException($"Synced property {prop.Name} must have BitIndex");
                    var syncAlways = attr.SyncAlways;

                    if (bitIndex > baseMaxBitIndex)
                        baseMaxBitIndex = bitIndex;

                    syncedProperties.Add(BuildSyncedProperty(prop, bitIndex, syncAlways ?? false));
                }

                currentType = currentType.BaseType;
            }

            // Add local properties
            var localProps = t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(p => ShouldIncludeProperty(p, onlySyncedProperties));

            foreach (var prop in localProps)
            {
                var attr = prop.GetCustomAttribute<SyncedAttribute>();
                if (attr is null) continue;

                var localBitIndex = attr.BitIndex;
                var globalBitIndex = baseMaxBitIndex + 1 + localBitIndex;

                syncedProperties.Add(BuildSyncedProperty(prop, globalBitIndex, attr.SyncAlways ?? false));
            }

            return (syncedProperties, syncedProperties.Count);
        });
    }

    private static SyncedProperty BuildSyncedProperty(PropertyInfo prop, int bitIndex, bool syncAlways)
    {
        var param = Expression.Parameter(typeof(object), "obj");
        var casted = Expression.Convert(param, prop.DeclaringType!);
        var access = Expression.Property(casted, prop);
        var convert = Expression.Convert(access, typeof(object));
        var lambda = Expression.Lambda<Func<object, object?>>(convert, param).Compile();

        return new SyncedProperty(prop.Name, bitIndex, syncAlways, lambda);
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
