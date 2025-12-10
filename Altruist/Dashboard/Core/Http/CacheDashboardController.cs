using System.Text.Json;

using Altruist.InMemory;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard
{
    public sealed class CacheEntryDto
    {
        public string Type { get; set; } = default!;        // assembly-qualified type
        public string TypeShortName { get; set; } = default!;
        public string GroupId { get; set; } = string.Empty;
        public string Key { get; set; } = default!;

        // The actual value as JSON
        public JsonElement Value { get; set; }

        // Optional preview string (e.g. truncated JSON)
        public string? Preview { get; set; }
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

    /// <summary>
    /// Dashboard controller exposing in-memory cache contents
    /// for inspection and live editing from the Angular UI.
    /// </summary>
    [ApiController]
    [Route("/dashboard/v1/cache")]
    [ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "inmemory")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class CacheDashboardController : ControllerBase
    {
        private readonly ICacheProvider _cacheProvider;
        private readonly JsonSerializerOptions _jsonOptions;

        public CacheDashboardController(
            ICacheProvider cacheProvider,
            JsonSerializerOptions jsonOptions)
        {
            _cacheProvider = cacheProvider;
            _jsonOptions = jsonOptions;
        }

        private static string GetShortTypeName(Type type)
        {
            var name = type.Name;
            var idx = name.IndexOf('`'); // strip generic arity
            return idx > 0 ? name[..idx] : name;
        }

        /// <summary>
        /// Stream all cache entries as NDJSON (one entry per line).
        /// </summary>
        [HttpGet("entries/stream")]
        public async Task StreamCacheEntries(CancellationToken ct)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/x-ndjson";

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
                    Preview = JsonSerializer.Serialize(jsonElement)
                };

                await JsonSerializer.SerializeAsync(Response.Body, dto, _jsonOptions, ct);
                await Response.WriteAsync("\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }

        /// <summary>
        /// Update a single cache entry's value (overwrite existing or create if not present).
        /// </summary>
        [HttpPut("entry")]
        public async Task<IActionResult> UpdateEntry(
            [FromBody] CacheEntryUpdateDto dto,
            CancellationToken ct)
        {
            var type = Type.GetType(dto.Type, throwOnError: true)
                       ?? throw new InvalidOperationException($"Unknown type: {dto.Type}");

            // Deserialize JSON to actual typed object
            var valueObj = JsonSerializer.Deserialize(
                               dto.Value.GetRawText(),
                               type,
                               _jsonOptions)
                           ?? throw new InvalidOperationException("Deserialized value is null.");

            // Use reflection to call SaveAsync<T>
            var method = typeof(InMemoryCache)
                .GetMethod(nameof(InMemoryCache.SaveAsync))!
                .MakeGenericMethod(type);

            var task = (Task)method.Invoke(
                _cacheProvider,
                [dto.Key, valueObj, dto.GroupId ?? string.Empty])!;

            await task;
            return NoContent();
        }

        /// <summary>
        /// Remove a single cache entry (by type, group and key).
        /// </summary>
        [HttpDelete("entry")]
        public async Task<IActionResult> DeleteEntry(
            [FromQuery] CacheEntryKeyDto dto,
            CancellationToken ct)
        {
            var type = Type.GetType(dto.Type, throwOnError: true)
                       ?? throw new InvalidOperationException($"Unknown type: {dto.Type}");

            var method = typeof(InMemoryCache)
                .GetMethod(nameof(InMemoryCache.RemoveAsync))!
                .MakeGenericMethod(type);

            var task = (Task)method.Invoke(
                _cacheProvider,
                [dto.Key, dto.GroupId ?? string.Empty])!;

            await task;
            return NoContent();
        }
    }
}
