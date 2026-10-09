using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard
{
    /// <summary>One cache entry as streamed by the <c>/dashboard/v1/cache/entries/*</c> endpoints (one JSON object per NDJSON line).</summary>
    public sealed class CacheEntryDto
    {
        /// <summary>Assembly-qualified type name of the value (send back as-is to edit or delete).</summary>
        public string Type { get; set; } = default!;
        /// <summary>Type name without namespace or generic arity.</summary>
        public string TypeShortName { get; set; } = default!;
        /// <summary>Cache group (empty for the default group).</summary>
        public string GroupId { get; set; } = string.Empty;
        /// <summary>Entry key.</summary>
        public string Key { get; set; } = default!;
        /// <summary>The value serialized as JSON.</summary>
        public JsonElement Value { get; set; }
        /// <summary>Compact JSON text of <see cref="Value"/>.</summary>
        public string? Preview { get; set; }

        /// <summary>
        /// Where this entry lives: "inmemory", "redis", or "both".
        /// </summary>
        public string Source { get; set; } = "inmemory";
    }

    /// <summary>Body of <c>PUT /dashboard/v1/cache/entry</c>.</summary>
    public sealed class CacheEntryUpdateDto
    {
        /// <summary>Assembly-qualified type name, as in <see cref="CacheEntryDto.Type"/>.</summary>
        public string Type { get; set; } = default!;
        /// <summary>Cache group (empty for the default group).</summary>
        public string GroupId { get; set; } = string.Empty;
        /// <summary>Entry key.</summary>
        public string Key { get; set; } = default!;
        /// <summary>New value as JSON; must deserialize to <see cref="CacheEntryUpdateDto.Type"/>.</summary>
        public JsonElement Value { get; set; }
    }

    /// <summary>Query parameters of <c>DELETE /dashboard/v1/cache/entry</c> (<c>?Type=&amp;GroupId=&amp;Key=</c>).</summary>
    public sealed class CacheEntryKeyDto
    {
        /// <summary>Assembly-qualified type name, as in <see cref="CacheEntryDto.Type"/>.</summary>
        public string Type { get; set; } = default!;
        /// <summary>Cache group (empty for the default group).</summary>
        public string GroupId { get; set; } = string.Empty;
        /// <summary>Entry key.</summary>
        public string Key { get; set; } = default!;
    }

    /// <summary>Response of <c>GET /dashboard/v1/cache/info</c>.</summary>
    public sealed class CacheInfoDto
    {
        /// <summary><c>redis</c> or <c>inmemory</c>.</summary>
        public string Provider { get; set; } = default!;
        /// <summary>Redis connection state; always <c>true</c> for the in-memory provider.</summary>
        public bool IsConnected { get; set; }
        /// <summary>Number of entries in the in-memory cache.</summary>
        public int InMemoryEntryCount { get; set; }
    }

    /// <summary>
    /// Dashboard controller exposing cache contents for inspection and live editing, route
    /// <c>/dashboard/v1/cache</c>. Works with both InMemory and Redis cache providers.
    /// </summary>
    /// <remarks>
    /// Only mapped when <c>altruist:dashboard:enabled</c> is <c>true</c>. No authentication is applied, and the
    /// edit/delete endpoints resolve any type name sent by the client via <see cref="Type.GetType(string, bool)"/>
    /// and write the in-memory tier; do not expose this to untrusted networks. Listing and edits cover the local
    /// in-memory tier only (Redis-only entries are not listed and edits are not written to Redis).
    /// </remarks>
    [ApiController]
    [Route("/dashboard/v1/cache")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class CacheDashboardController : ControllerBase
    {
        private readonly ICacheProvider _cacheProvider;
        private readonly IMemoryCacheProvider _memoryCacheProvider;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>Creates the controller.</summary>
        /// <param name="cacheProvider">The configured cache provider (in-memory or Redis).</param>
        /// <param name="memoryCacheProvider">The in-memory cache.</param>
        /// <param name="jsonOptions">Options for (de)serializing entry values.</param>
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
        /// <c>GET /dashboard/v1/cache/info</c>: 200 with a <see cref="CacheInfoDto"/>: provider (<c>redis</c> when the
        /// provider is an <see cref="IRedisCacheProvider"/>, else <c>inmemory</c>), Redis connection state (always true
        /// for in-memory) and the in-memory entry count.
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
        /// <c>GET /dashboard/v1/cache/entries/stream</c>: 200 <c>application/x-ndjson</c>, one <see cref="CacheEntryDto"/>
        /// per line from the configured provider's <see cref="ICacheProvider.GetSnapshot"/>. With Redis this is still the
        /// in-memory layer; <see cref="CacheEntryDto.Source"/> is then labelled <c>inmemory+redis</c>.
        /// </summary>
        /// <param name="ct">Aborts the stream.</param>
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
        /// <c>GET /dashboard/v1/cache/entries/inmemory/stream</c>: 200 <c>application/x-ndjson</c>, one
        /// <see cref="CacheEntryDto"/> per line from the in-memory cache (source <c>inmemory</c>).
        /// </summary>
        /// <param name="ct">Aborts the stream.</param>
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
        /// <c>PUT /dashboard/v1/cache/entry</c> with a <see cref="CacheEntryUpdateDto"/> body: deserializes the value as the
        /// named type and calls <see cref="ICacheProvider.SaveAsync{T}"/> (local tier). 204 on success; an unknown type or
        /// null value throws (500).
        /// </summary>
        /// <param name="dto">Entry type, group, key and new JSON value.</param>
        /// <param name="ct">Not observed.</param>
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
        /// <c>DELETE /dashboard/v1/cache/entry?Type=&amp;GroupId=&amp;Key=</c>: calls <see cref="ICacheProvider.RemoveAsync{T}"/>
        /// (local tier) for the named type. 204 on completion; an unknown type throws (500).
        /// </summary>
        /// <param name="dto">Entry type, group and key.</param>
        /// <param name="ct">Not observed.</param>
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
