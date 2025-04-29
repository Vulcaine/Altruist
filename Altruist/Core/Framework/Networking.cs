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

using System.Collections.Concurrent;
using System.Reflection;
using Altruist.UORM;
using Microsoft.Xna.Framework;

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


public static class Synchronization
{
    private static readonly ConcurrentDictionary<Type, (PropertyInfo[], int)> _syncMetadata = new();
    private static readonly ConcurrentDictionary<string, object?[]> _lastSyncedStates = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object?>> _lastSyncedData = new();

    private static object _syncLock = new();

    // lazy load sync metadata, after warmup syncing will be super fast!
    private static (PropertyInfo[], int) GetSyncMetadata(Type type, bool onlySyncedProperties = true)
    {
        return _syncMetadata.GetOrAdd(type, t =>
        {
            var properties = new List<PropertyInfo>();
            int baseMaxBitIndex = 0;

            // Traverse inheritance chain first (base classes first)
            var currentType = t.BaseType;
            while (currentType != null && currentType != typeof(object))
            {
                var baseProperties = currentType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => ShouldIncludeProperty(p, onlySyncedProperties))
                    .ToArray();

                if (onlySyncedProperties)
                {
                    // Get max BitIndex among base class properties
                    var baseBitIndexes = baseProperties
                        .Select(p => p.GetCustomAttribute<SyncedAttribute>()?.BitIndex ?? -1)
                        .Where(index => index >= 0);

                    if (baseBitIndexes.Any())
                        baseMaxBitIndex = Math.Max(baseMaxBitIndex, baseBitIndexes.Max());
                }

                properties.AddRange(baseProperties);
                currentType = currentType.BaseType;
            }

            // Now add properties from the current type (DeclaredOnly ensures no base duplication)
            var localProperties = t.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(p => ShouldIncludeProperty(p, onlySyncedProperties))
                .ToArray();

            if (onlySyncedProperties)
            {
                foreach (var prop in localProperties)
                {
                    var attr = prop.GetCustomAttribute<SyncedAttribute>();
                    if (attr != null)
                    {
                        // Offset BitIndex
                        attr.BitIndex += baseMaxBitIndex + 1;
                    }
                }
            }

            properties.AddRange(localProperties);
            return (properties.ToArray(), properties.Count);
        });
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

    public static (ulong[], Dictionary<string, object?>) GetChangedData<TType>(TType newEntity, string clientId, bool forceAllAsChanged = false) where TType : ISynchronizedEntity
    {
        lock (_syncLock)
        {
            var (properties, count) = GetSyncMetadata(newEntity.GetType(), onlySyncedProperties: !forceAllAsChanged);

            int maskCount = (count + 63) / 64; // 1 ulong per 64 properties
            var masks = new ulong[maskCount];

            var lastState = _lastSyncedStates.GetOrAdd(clientId, _ => new object[count]);
            var changedData = _lastSyncedData.GetOrAdd(clientId, _ => new ConcurrentDictionary<string, object?>());
            changedData.Clear();

            var syncAlwaysProperties = new List<int>(); // Collect the indices of SyncAlways properties
            bool nonSyncAlwaysChanged = false; // Track if any non-SyncAlways property has changed

            // First pass: collect SyncAlways properties
            for (int i = 0; i < count; i++)
            {
                var propertyName = properties[i].Name;
                var propertySyncedAttribute = properties[i].GetCustomAttribute<SyncedAttribute>();
                var newValue = properties[i].GetValue(newEntity);
                var lastStateValue = lastState[i];

                bool isSyncAlways = propertySyncedAttribute != null && propertySyncedAttribute.SyncAlways == true;
                bool shouldSync = forceAllAsChanged || !AreValuesEqual(newValue, lastStateValue);

                if (isSyncAlways)
                {
                    // Collect SyncAlways property indices
                    syncAlwaysProperties.Add(i);
                }

                if (shouldSync)
                {
                    // For non-SyncAlways properties, mark them as changed and set the flag
                    nonSyncAlwaysChanged = true;

                    int maskIndex = i / 64;   // Which ulong
                    int bitIndex = i % 64;    // Which bit inside the ulong
                    masks[maskIndex] |= 1UL << bitIndex;

                    changedData[propertyName] = newValue;
                    lastState[i] = newValue;
                }
            }

            // Second pass: If any non-SyncAlways property has changed, mark all SyncAlways properties
            if (nonSyncAlwaysChanged)
            {
                foreach (var i in syncAlwaysProperties)
                {
                    var propertyName = properties[i].Name;
                    var newValue = properties[i].GetValue(newEntity);
                    int maskIndex = i / 64;   // Which ulong
                    int bitIndex = i % 64;    // Which bit inside the ulong
                    masks[maskIndex] |= 1UL << bitIndex;

                    changedData[propertyName] = newValue;
                    lastState[i] = newValue;
                }
            }

            return (masks, changedData.ToDictionary());
        }
    }

    private static bool AreValuesEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        // Handle Vector2 explicitly
        if (a is Vector2 va && b is Vector2 vb)
            return va.Equals(vb);

        // Handle arrays
        if (a is Array arrayA && b is Array arrayB)
        {
            if (arrayA.Length != arrayB.Length) return false;

            for (int i = 0; i < arrayA.Length; i++)
            {
                var itemA = arrayA.GetValue(i);
                var itemB = arrayB.GetValue(i);

                if (!AreValuesEqual(itemA, itemB))
                    return false;
            }

            return true;
        }

        // Fallback to default equality
        return a.Equals(b);
    }
}
