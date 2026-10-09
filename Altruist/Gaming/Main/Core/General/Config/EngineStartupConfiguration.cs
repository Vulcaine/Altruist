
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

using Altruist.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Gaming.Engine;

/// <summary>
/// Engine startup hook. It used to build a throwaway service provider here, which constructed
/// every singleton a second time and started a second engine on it. Configuration now only
/// registers; the engine lifecycle runs on the root provider:
/// <list type="bullet">
/// <item><c>[Cycle]</c> methods are registered by <see cref="Altruist.Engine.MethodScheduler"/>'s
/// <c>[PostConstruct]</c>, on the instances the root provider built;</item>
/// <item>the visibility tracker and the 2D/3D world organizer wire each other in the
/// trackers' <c>[PostConstruct]</c> (<c>WireOrganizer</c>);</item>
/// <item>the engine is started only by <c>ServerStatus</c> once every connectable service is up.</item>
/// </list>
/// </summary>
[ServiceConfiguration]
[ConditionalOnConfig("altruist:game:engine")]
public class EngineStartupConfiguration : IAltruistConfiguration
{
    /// <summary>Set once <see cref="Configure"/> ran.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Registers nothing; only marks the engine configuration as done (active when <c>altruist:game:engine</c> is configured).</summary>
    /// <param name="services">The service collection being built (unused).</param>
    public Task Configure(IServiceCollection services)
    {
        IsConfigured = true;
        return Task.CompletedTask;
    }
}
