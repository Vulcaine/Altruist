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

using Altruist.Persistence;

namespace Altruist.ScyllaDB;

public static class Extension
{
    public static AltruistApplicationBuilder WithScyllaDB(this AltruistDatabaseBuilder builder, Func<ScyllaDBConnectionSetup, ScyllaDBConnectionSetup>? setup = null)
    {
        return builder.SetupDatabase(ScyllaDBToken.Instance, setup);
    }

    public static KeyspaceSetup<TKeyspace> ForgeVaults<TKeyspace>(this KeyspaceSetup<TKeyspace> setup)
     where TKeyspace : class, IKeyspace
    {
        var vaultModelTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IVaultModel).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false });

        foreach (var type in vaultModelTypes)
        {
            setup.AddVault(type);
        }

        return setup;
    }

}