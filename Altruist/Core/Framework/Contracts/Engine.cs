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

namespace Altruist.Engine;


public class TaskIdentifier : IEquatable<TaskIdentifier>
{
    public string Id { get; }

    public TaskIdentifier(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }

    public bool Equals(TaskIdentifier? other)
    {
        if (other == null) return false;
        return Id.Equals(other.Id, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public static TaskIdentifier FromType(Type type)
    {
        return new TaskIdentifier(type.FullName!);
    }

    public override string ToString() => Id;
}


public interface IAltruistEngine
{
    public CycleRate Rate { get; }

    public bool Enabled { get; }

    void Enable();

    void Disable();

    void Start();
    void Stop();

    void ScheduleTask(Delegate taskDelegate, CycleRate? frequencyHz = null);
    void SendTask(TaskIdentifier taskId, Delegate taskDelegate);
    void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null);
}