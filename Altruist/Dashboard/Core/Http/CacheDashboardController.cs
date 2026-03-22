using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard
{
    public sealed class CacheEntryDto
    {
        public string Type { get; set; } = default!;
        public string TypeShortName { get; set; } = default!;
        public string GroupId { get; set; } = string.Empty;
        public string Key { get; set; } = default!;
        public JsonElement Value { get; set; }
        public string? Preview { get; set; }

        /// <summary>
        /// Where this entry lives: "inmemory", "redis", or "both".
        /// </summary>
        public string Source { get; set; } = "inmemory";
    }

    public sealed class CacheEntryUpdateDto
    {
        public string Type { get; set; } = default!;
        public string GroupId { get; set; } = string.Empty;
        public string Key { get; set; } = default!;
        public JsonElement Value { get; set; }
    }

    public sealed class CacheEntryKeyDto
    {
        public string Type { get; set; } = default!;
        public string GroupId { get; set; } = string.Empty;
        public string Key { get; set; } = default!;
    }

    public sealed class CacheInfoDto
    {
        public string Provider { get; set; } = default!;
        public bool IsConnected { get; set; }
        public int InMemoryEntryCount { get; set; }
    }

    /// <summary>
    /// Dashboard controller exposing cache contents for inspection and live editing.
    /// Works with both InMemory and Redis cache providers.
    /// </summary>
    [ApiController]
    [Route("/dashboard/v1/cache")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class CacheDashboardController : ControllerBase
    {
        private readonly ICacheProvider _cacheProvider;
        private readonly IMemoryCacheProvider _memoryCacheProvider;
        private readonly JsonSerializerOptions _jsonOptions;

        public CacheDashboardController(
            ICacheProvider cacheProvider,
            IMemoryCacheProvider memoryCacheProvider,
            JsonSerializerOptions jsonOptions)
        {
            _cacheProvider = cacheProvider;
            _memoryCacheProvider = memoryCacheProvider;
            _jsonOptions = jsonOptions;
        }

        private static string GetShortTypeName(Type type)
        {
            var name = type.Name;
            var idx = name.IndexOf('`');
            return idx > 0 ? name[..idx] : name;
        }

        /// <summary>
        /// Get cache provider info (provider type, connection status).
        /// </summary>
        [HttpGet("info")]
        public IActionResult GetCacheInfo()
        {
            var providerName = _cacheProvider is IRedisCacheProvider
                ? "redis"
                : "inmemory";

            var isConnected = _cacheProvider is IRedisCacheProvider redis
                ? redis.IsConnected
                : true;

            var inMemoryCount = _memoryCacheProvider.GetSnapshot().Count();

            return Ok(new CacheInfoDto
            {
                Provider = providerName,
                IsConnected = isConnected,
                InMemoryEntryCount = inMemoryCount
            });
        }

        /// <summary>
        /// Stream all in-memory cache entries as NDJSON.
        /// When Redis is the provider, entries shown are from the in-memory layer.
        /// </summary>
        [HttpGet("entries/stream")]
        public async Task StreamCacheEntries(CancellationToken ct)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/x-ndjson";

            var source = _cacheProvider is IRedisCacheProvider ? "inmemory+redis" : "inmemory";

            foreach (var snapshot in _cacheProvider.GetSnapshot())
            {
                ct.ThrowIfCancellationRequested();

                var jsonElement = JsonSerializer.SerializeToElement(
                    snapshot.Value,
                    snapshot.Type,
                    _jsonOptions);

                var dto = new CacheEntryDto
                {
                    Type = snapshot.Type.AssemblyQualifiedName
                           ?? snapshot.Type.FullName
                           ?? snapshot.Type.Name,
                    TypeShortName = GetShortTypeName(snapshot.Type),
                    GroupId = snapshot.GroupId ?? string.Empty,
                    Key = snapshot.Key,
                    Value = jsonElement,
                    Preview = JsonSerializer.Serialize(jsonElement),
                    Source = source
                };

                await JsonSerializer.SerializeAsync(Response.Body, dto, _jsonOptions, ct);
                await Response.WriteAsync("\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }

        /// <summary>
        /// Stream in-memory only cache entries (always available, fast).
        /// </summary>
        [HttpGet("entries/inmemory/stream")]
        public async Task StreamInMemoryCacheEntries(CancellationToken ct)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/x-ndjson";

            foreach (var snapshot in _memoryCacheProvider.GetSnapshot())
            {
                ct.ThrowIfCancellationRequested();

                var jsonElement = JsonSerializer.SerializeToElement(
                    snapshot.Value,
                    snapshot.Type,
                    _jsonOptions);

                var dto = new CacheEntryDto
                {
                    Type = snapshot.Type.AssemblyQualifiedName
                           ?? snapshot.Type.FullName
                           ?? snapshot.Type.Name,
                    TypeShortName = GetShortTypeName(snapshot.Type),
                    GroupId = snapshot.GroupId ?? string.Empty,
                    Key = snapshot.Key,
                    Value = jsonElement,
                    Preview = JsonSerializer.Serialize(jsonElement),
                    Source = "inmemory"
                };

                await JsonSerializer.SerializeAsync(Response.Body, dto, _jsonOptions, ct);
                await Response.WriteAsync("\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }

        /// <summary>
        /// Update a single cache entry's value.
        /// </summary>
        [HttpPut("entry")]
        public async Task<IActionResult> UpdateEntry(
            [FromBody] CacheEntryUpdateDto dto,
            CancellationToken ct)
        {
            var type = Type.GetType(dto.Type, throwOnError: true)
                       ?? throw new InvalidOperationException($"Unknown type: {dto.Type}");

            var valueObj = JsonSerializer.Deserialize(
                               dto.Value.GetRawText(),
                               type,
                               _jsonOptions)
                           ?? throw new InvalidOperationException("Deserialized value is null.");

            var method = typeof(ICacheProvider)
                .GetMethod(nameof(ICacheProvider.SaveAsync))!
                .MakeGenericMethod(type);

            var task = (Task)method.Invoke(
                _cacheProvider,
                [dto.Key, valueObj, dto.GroupId ?? string.Empty])!;

            await task;
            return NoContent();
        }

        /// <summary>
        /// Remove a single cache entry.
        /// </summary>
        [HttpDelete("entry")]
        public async Task<IActionResult> DeleteEntry(
            [FromQuery] CacheEntryKeyDto dto,
            CancellationToken ct)
        {
            var type = Type.GetType(dto.Type, throwOnError: true)
                       ?? throw new InvalidOperationException($"Unknown type: {dto.Type}");

            var method = typeof(ICacheProvider)
                .GetMethod(nameof(ICacheProvider.RemoveAsync))!
                .MakeGenericMethod(type);

            var task = (Task)method.Invoke(
                _cacheProvider,
                [dto.Key, dto.GroupId ?? string.Empty])!;

            await task;
            return NoContent();
        }
    }
}
