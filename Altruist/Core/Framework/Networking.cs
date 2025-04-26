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
    private static readonly ConcurrentDictionary<string, Dictionary<string, object?>> _lastSyncedData = new();

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
        var (properties, count) = GetSyncMetadata(newEntity.GetType(), onlySyncedProperties: !forceAllAsChanged);

        int maskCount = (count + 63) / 64; // 1 ulong per 64 properties
        var masks = new ulong[maskCount];

        var lastState = _lastSyncedStates.GetOrAdd(clientId, _ => new object[count]);
        var changedData = _lastSyncedData.GetOrAdd(clientId, _ => new Dictionary<string, object?>(count));
        changedData.Clear();

        for (int i = 0; i < count; i++)
        {
            var propertyName = properties[i].Name;
            var newValue = properties[i].GetValue(newEntity);

            if (!Equals(lastState[i], newValue) || forceAllAsChanged)
            {
                int maskIndex = i / 64;   // Which ulong
                int bitIndex = i % 64;    // Which bit inside the ulong
                masks[maskIndex] |= 1UL << bitIndex;

                changedData[propertyName] = newValue;
                lastState[i] = newValue;
            }
        }

        return (masks, changedData);
    }
}
