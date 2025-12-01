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
    /// Called once, after schemas and tables are created.
    /// Return any vault model instances that should be saved.
    /// You may return different model types in the same list.
    /// </summary>
    Task<List<IVaultModel>> InitializeAsync(IServiceProvider sp);
}
