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

namespace Altruist.Networking;

[AttributeUsage(AttributeTargets.Property)]
public class SyncedAttribute : Attribute
{
    public int BitIndex { get; }
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


    private static (PropertyInfo[], int) GetSyncMetadata(Type type)
    {
        return _syncMetadata.GetOrAdd(type, t =>
        {
            var properties = t.GetProperties()
                              .Where(p => Attribute.IsDefined(p, typeof(SyncedAttribute)))
                              .OrderBy(p => p.GetCustomAttribute<SyncedAttribute>()!.BitIndex)
                              .ToArray();
            return (properties, properties.Length);
        });
    }

    public static (ulong, Dictionary<string, object?>) GetChangedData<TType>(TType newEntity, string clientId) where TType : ISynchronizedEntity
    {
        var (properties, count) = GetSyncMetadata(typeof(TType));

        ulong mask = 0;

        var lastState = _lastSyncedStates.GetOrAdd(clientId, _ => new object[count]);
        var changedData = _lastSyncedData.GetOrAdd(clientId, _ => new Dictionary<string, object?>(count));
        changedData.Clear();

        for (int i = 0; i < count; i++)
        {
            var propertyName = properties[i].Name;
            var newValue = properties[i].GetValue(newEntity);

            if (!Equals(lastState[i], newValue))
            {
                mask |= 1UL << i;
                changedData[propertyName] = newValue;
                lastState[i] = newValue;
            }
        }

        return (mask, changedData);
    }


}
