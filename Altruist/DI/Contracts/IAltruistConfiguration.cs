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

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Contracts;

public interface IAltruistConfiguration
{
    /// <summary>
    /// True once Configure has successfully completed for this instance.
    /// Used to prevent double-configuration and to satisfy [Service.DependsOn].
    /// </summary>
    bool IsConfigured { get; set; }

    /// <summary>
    /// Apply configuration to the given service collection.
    /// Implementations should be idempotent.
    /// </summary>
    Task Configure(IServiceCollection services);
}
