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

namespace Altruist.EFCore;

// public class EfCoreDatabaseProvider : ILinqDatabaseProvider
// {
//     public DbContext Context { get; }

//     public EfCoreDatabaseProvider(DbContext context)
//     {
//         Context = context;
//     }

//     public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(Expression<Func<TVaultModel, bool>> filter) where TVaultModel : class, IVaultModel
//     {
//         return await Context.Set<TVaultModel>().Where(filter).ToListAsync();
//     }

//     public async Task<int> UpdateAsync<TVaultModel>(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls) where TVaultModel : class, IVaultModel
//     {
//         var entitiesToUpdate = Context.Set<TVaultModel>();

//         return await entitiesToUpdate
//             .ExecuteUpdateAsync(setPropertyCalls);
//     }

//     Task IGeneralDatabaseProvider.CreateTableAsync<TVaultModel>(IKeyspace? keyspace = null)
//     {
//         throw new NotImplementedException();
//     }

//     public Task CreateTableAsync(Type vaultModel, IKeyspace? keyspace = null)
//     {
//         throw new NotImplementedException();
//     }

//     public Task CreateKeySpaceAsync(string keyspace, ReplicationOptions? options = null)
//     {
//         throw new NotImplementedException();
//     }

//     public Task ChangeKeyspaceAsync(string keyspace)
//     {
//         throw new NotImplementedException();
//     }
// }

