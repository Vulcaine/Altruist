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

namespace Altruist.Persistence;

public interface IDatabaseInitializer
{
    /// <summary>
    /// Lower numbers run first. Higher numbers run later.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Return vault models to seed.
    /// </summary>
    Task<IEnumerable<IVaultModel>> InitializeAsync(IServiceProvider services);
}

