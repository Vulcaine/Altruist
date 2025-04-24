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

using System.Linq.Expressions;
using System.Reflection;
using Altruist;
using Microsoft.Extensions.DependencyInjection;

public static class EventHandlerRegistry<TType>
{
    private static readonly Dictionary<string, Delegate> _eventHandlers = new();

    // Method to scan and register event handlers for any type T (e.g., IPortal, ITemple)
    public static void ScanAndRegisterHandlers(IServiceProvider serviceProvider)
    {
        var instances = serviceProvider.GetServices<TType>(); // Get all instances of T (e.g., IPortal or ITemple)
        foreach (var instance in instances)
        {
            RegisterEventHandlers(instance);
        }
    }

    // Register event handlers from a specific instance
    private static void RegisterEventHandlers(TType instance)
    {
        var methods = instance!.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<GateAttribute>() != null);

        foreach (var method in methods)
        {
            var attribute = method.GetCustomAttribute<GateAttribute>();
            var parameters = method.GetParameters();

            if (attribute != null)
            {
                ValidateMethod(method, parameters);

                var delegateType = Expression.GetDelegateType(parameters.Select(p => p.ParameterType).Concat(new[] { method.ReturnType }).ToArray());
                var @delegate = method.CreateDelegate(delegateType, instance);

                _eventHandlers[attribute.Event] = @delegate;
            }
        }
    }

    // Validate the method to ensure it adheres to the correct signature for event handlers
    private static void ValidateMethod(MethodInfo method, ParameterInfo[] parameters)
    {
        if (method.ReturnType != typeof(Task))
        {
            throw new InvalidOperationException($"Method {method.Name} marked with [Gate] must return Task.");
        }

        if (parameters.Length == 1)
        {
            if (!typeof(IPacket).IsAssignableFrom(parameters[0].ParameterType))
            {
                throw new InvalidOperationException($"Method {method.Name} marked with [Gate] must have the first parameter as a subtype of IPacket.");
            }
        }
        else if (parameters.Length == 2)
        {
            if (!typeof(IPacket).IsAssignableFrom(parameters[0].ParameterType) || parameters[1].ParameterType != typeof(string))
            {
                throw new InvalidOperationException($"Method {method.Name} marked with [Gate] must have exactly two parameters: (IPacket, string).");
            }
        }
        else
        {
            throw new InvalidOperationException($"Method {method.Name} marked with [Gate] must have exactly 1 or 2 parameters.");
        }
    }

    // Try to retrieve an event handler for a specific event name
    public static bool TryGetHandler(string eventName, out Delegate handler)
    {
        return _eventHandlers.TryGetValue(eventName, out handler!);
    }
}

