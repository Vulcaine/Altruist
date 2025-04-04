using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Query;
using StackExchange.Redis;

namespace Altruist.Redis;

// public class RedisQuery
// {
//     public string FilterPredicate { get; set; } = "";
//     public string OrderByDirection { get; set; } = "asc"; // Default: ascending
//     public int SkipCount { get; set; } = 0;
//     public int TakeCount { get; set; } = 10; // Default to 10 items

//     // Adds a WHERE condition (a simple predicate for now, can be enhanced later)
//     public RedisQuery Where(string filterPredicate)
//     {
//         FilterPredicate = filterPredicate;
//         return this;
//     }

//     // Adds ORDER BY condition
//     public RedisQuery OrderBy(string direction = "asc")
//     {
//         OrderByDirection = direction;
//         return this;
//     }

//     // Adds pagination (Skip)
//     public RedisQuery Skip(int count)
//     {
//         SkipCount = count;
//         return this;
//     }

//     // Adds pagination (Take)
//     public RedisQuery Take(int count)
//     {
//         TakeCount = count;
//         return this;
//     }

//     // Determines if the Lua script is necessary
//     public bool NeedsLuaScript()
//     {
//         return !string.IsNullOrEmpty(FilterPredicate) || OrderByDirection != "asc" || SkipCount > 0 || TakeCount < 10;
//     }

//     // Builds the Lua script dynamically based on the query state
//     public string BuildLuaScript(string keyPattern)
//     {
//         return @"
//             local result = {}
//             local keys = redis.call('KEYS', ARGV[1])  -- Get all keys matching pattern

//             for i, key in ipairs(keys) do
//                 local value = redis.call('GET', key)  -- Retrieve value of key
//                 if value then
//                     -- Apply filtering condition
//                     if string.match(value, ARGV[2]) then
//                         table.insert(result, {key = key, value = value})
//                     end
//                 end
//             end

//             -- Apply sorting based on order direction
//             if ARGV[3] == 'asc' then
//                 table.sort(result, function(a, b) return a.value < b.value end)
//             elseif ARGV[3] == 'desc' then
//                 table.sort(result, function(a, b) return a.value > b.value end)
//             end

//             -- Apply pagination (skip and take)
//             local startIndex = tonumber(ARGV[4]) or 0
//             local endIndex = tonumber(ARGV[5]) or #result
//             local paginatedResults = {}

//             for i = startIndex, math.min(endIndex, #result) do
//                 table.insert(paginatedResults, result[i])
//             end

//             return paginatedResults
//         ";
//     }

//     // Returns the parameters for the Lua script
//     public RedisValue[] GetLuaScriptParams(string keyPattern)
//     {
//         return new RedisValue[]
//         {
//             keyPattern,         // Key pattern to match in Redis
//             FilterPredicate,    // Filter (WHERE condition)
//             OrderByDirection,   // Order direction ('asc' or 'desc')
//             SkipCount.ToString(), // Skip count
//             TakeCount.ToString()  // Take count
//         };
//     }
// }

// public class RedisVault<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
// {
//     private RedisCacheProvider _cacheProvider;
//     private RedisQuery _query;

//     public IKeyspace Keyspace => throw new NotImplementedException();

//     public RedisVault(RedisCacheProvider cacheProvider)
//     {
//         _cacheProvider = cacheProvider;
//         _query = new RedisQuery(); // Initialize with a fresh query object
//     }

//     // Build the WHERE clause (filter)
//     public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
//     {
//         var compiledPredicate = predicate.Compile();
//         _query.Where(compiledPredicate.ToString()); // Simplified, need better handling
//         return this;
//     }

//     // Build the ORDER BY clause (ascending)
//     public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
//     {
//         _query.OrderBy("asc"); // Default order is ascending
//         return this;
//     }

//     // Build the ORDER BY clause (descending)
//     public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
//     {
//         _query.OrderBy("desc"); // Descending order
//         return this;
//     }

//     // Build the SKIP clause (pagination)
//     public IVault<TVaultModel> Skip(int count)
//     {
//         _query.Skip(count);
//         return this;
//     }

//     // Build the TAKE clause (pagination)
//     public IVault<TVaultModel> Take(int count)
//     {
//         _query.Take(count);
//         return this;
//     }

//     // Execute the query and get the results
//     public async Task<ICursor<TVaultModel>> ToListAsync()
//     {
//         if (_query.NeedsLuaScript())
//         {
//             var script = _query.BuildLuaScript("someKeyPattern");
//             var parameters = _query.GetLuaScriptParams("someKeyPattern");

//             var redisResults = await _cacheProvider.GetDatabase().ScriptEvaluateAsync(
//                 script,
//                 new RedisKey[] { },
//                 parameters
//             );

//             // Parse the results
//             var resultList = new List<TVaultModel>();
//             foreach (var item in (RedisArray)redisResults)
//             {
//                 var value = item[1].ToString();
//                 var deserialized = JsonSerializer.Deserialize<TVaultModel>(value);
//                 resultList.Add(deserialized);
//             }

//             return resultList;
//         }
//         else
//         {
//             return await _cacheProvider.GetAllAsync<TVaultModel>();
//         }
//     }

//     public Task<TVaultModel?> FirstOrDefaultAsync()
//     {
//         throw new NotImplementedException();
//     }

//     public Task<TVaultModel?> FirstAsync()
//     {
//         throw new NotImplementedException();
//     }

//     public Task<ICursor<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate)
//     {
//         throw new NotImplementedException();
//     }

//     public async Task<int> CountAsync()
//     {
//         if (_query.NeedsLuaScript())
//         {
//             var script = _query.BuildLuaScript("someKeyPattern");
//             var parameters = _query.GetLuaScriptParams("someKeyPattern");

//             var redisResults = await _cacheProvider.GetDatabase().ScriptEvaluateAsync(
//                 script,
//                 [],
//                 parameters
//             );

//             return (int)redisResults;
//         }
//         else
//         {
//             var modelTypeName = typeof(TVaultModel).Name.ToLower();
//             var server = _cacheProvider.GetDatabase().Multiplexer.GetServer(_cacheProvider.GetDatabase().Multiplexer.GetEndPoints().First());
//             var keys = server.Keys(pattern: $"{modelTypeName}:*").ToArray();
//             return keys.Length;
//         }
//     }

//     public async Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
//     {
//         await _cacheProvider.SaveAsync(entity.Id, entity);
//     }

//     public async Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
//     {
//         var entityDict = entities.ToDictionary(e => e.Id);
//         await _cacheProvider.SaveBatchAsync(entityDict);
//     }

//     public async Task<int> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
//     {
//         // For simplicity, let's assume we have an in-memory model for SetPropertyCalls
//         // Fetch the existing entities, apply the updates, and save back to Redis
//         var items = await ToListAsync();  // Get the current list of items
//         var updatedCount = 0;

//         foreach (var item in items)
//         {
//             // Here we could apply the setPropertyCalls logic to update the item
//             // Example: setPropertyCalls.Invoke(new SetPropertyCalls<TVaultModel>(item));

//             await SaveAsync(item);  // Save the updated item
//             updatedCount++;
//         }

//         return updatedCount;
//     }

//     public async Task<bool> DeleteAsync()
//     {
//         var items = await ToListAsync(); // Get the items, assuming you want to delete all matching items
//         foreach (var item in items)
//         {
//             await _cacheProvider.RemoveAsync<TVaultModel>(item.Id);
//         }

//         return true; // Return true if delete was successful
//     }

//     public async Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
//     {
//         var items = await ToListAsync();
//         var compiledPredicate = predicate.Compile();
//         return items.Any(compiledPredicate);
//     }

//     public async Task<IEnumerable<TResult>> SelectAsync<TResult>(Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel
//     {
//         var items = await ToListAsync();
//         return items.Select(selector.Compile());
//     }
// }

