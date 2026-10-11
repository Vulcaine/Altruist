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

using System.Reflection;
using System.Text.Json;

using Altruist.Contracts;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Altruist.Dashboard;

/// <summary>
/// Dashboard overview API (route <c>/dashboard/v1/summary</c>): the app's <c>altruist*</c> configuration, the
/// registered portals/services/factories/configurations, engine settings, and live editing of config keys marked
/// live-editable.
/// </summary>
/// <remarks>
/// Only mapped when <c>altruist:dashboard:enabled</c> is <c>true</c>; every request must pass the dashboard protection
/// (<see cref="DashboardAccessOptions"/>). Secret values (keys matching password, secret, token, key, credential or
/// connection-string, and values that look like connection strings with a password) are returned as <c>***</c>
/// (<see cref="DashboardAccess.RedactValue"/>). The update endpoints change running configuration (live-editable keys only).
/// </remarks>
[ApiController]
[Route("/dashboard/v1/summary")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class AltruistSummaryDashboardController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly EngineConfigOptions? _engineOptions;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>Creates the controller.</summary>
    /// <param name="configuration">App configuration (read, and written through its mutable provider).</param>
    /// <param name="engineOptions">Engine settings reported in <see cref="AltruistSummaryDto.Engine"/>; registered when <c>altruist:game:engine</c> exists, otherwise null (no engine info).</param>
    /// <param name="jsonOptions">JSON options.</param>
    /// <param name="serviceProvider">Container whose services are listed.</param>
    public AltruistSummaryDashboardController(
        IConfiguration configuration,
        JsonSerializerOptions jsonOptions,
        IServiceProvider serviceProvider,
        EngineConfigOptions? engineOptions = null)
    {
        _configuration = configuration;
        _engineOptions = engineOptions;
        _jsonOptions = jsonOptions;
        _serviceProvider = serviceProvider;
    }

    // ------------------ DTOs ------------------

    /// <summary>One configuration key/value; also the body of the config update endpoints.</summary>
    public sealed class ConfigEntryDto
    {
        /// <summary>Full configuration path (e.g. <c>altruist:dashboard:enabled</c>).</summary>
        public string Key { get; set; } = default!;
        /// <summary>Current value (string form).</summary>
        public string? Value { get; set; }
        /// <summary>Whether the key is live-editable through the update endpoints.</summary>
        public bool Modifiable { get; set; }
    }

    /// <summary>A 3-component vector (engine gravity).</summary>
    public sealed class Vector3Dto
    {
        /// <summary>X component.</summary>
        public float X { get; set; }
        /// <summary>Y component.</summary>
        public float Y { get; set; }
        /// <summary>Z component.</summary>
        public float Z { get; set; }
    }

    /// <summary>Engine settings from <see cref="EngineConfigOptions"/>.</summary>
    public sealed class EngineInfoDto
    {
        /// <summary>Engine diagnostics flag.</summary>
        public bool Diagnostics { get; set; }
        /// <summary>Engine tick rate in Hz.</summary>
        public int FramerateHz { get; set; }
        /// <summary>Unit of the tick rate setting.</summary>
        public string Unit { get; set; } = "Ticks";
        /// <summary>Engine throttle setting, if any.</summary>
        public int? Throttle { get; set; }
        /// <summary>Configured gravity, or null when it could not be read.</summary>
        public Vector3Dto? Gravity { get; set; }
    }

    /// <summary>Why a type appears in the service list.</summary>
    public enum ServiceCategoryDto
    {
        /// <summary>Has a <c>[Portal]</c> attribute (one entry per attribute).</summary>
        Portal,
        /// <summary>Has a <c>[Service]</c> attribute (one entry per attribute).</summary>
        Service,
        /// <summary>Implements <see cref="IServiceFactory"/>.</summary>
        ServiceFactory,
        /// <summary>Implements <see cref="IAltruistConfiguration"/>.</summary>
        ServiceConfiguration
    }

    /// <summary>One registered type (a type can appear once per matching category/attribute).</summary>
    public sealed class ServiceInfoDto
    {
        /// <summary>Type name.</summary>
        public string Name { get; set; } = default!;
        /// <summary>Full type name.</summary>
        public string FullName { get; set; } = default!;
        /// <summary>Assembly name.</summary>
        public string Assembly { get; set; } = default!;
        /// <summary>Why this entry is listed.</summary>
        public ServiceCategoryDto Category { get; set; }

        // Only for [Service]
        /// <summary>DI lifetime (<see cref="ServiceCategoryDto.Service"/> only).</summary>
        public string? Lifetime { get; set; }
        /// <summary>Registered service type (<see cref="ServiceCategoryDto.Service"/> only).</summary>
        public string? ServiceType { get; set; }

        // Only for [Portal]
        /// <summary>Portal endpoint (<see cref="ServiceCategoryDto.Portal"/> only).</summary>
        public string? Endpoint { get; set; }
        /// <summary>Portal context (<see cref="ServiceCategoryDto.Portal"/> only).</summary>
        public string? Context { get; set; }
    }

    /// <summary>Response of <c>GET /dashboard/v1/summary</c>.</summary>
    public sealed class AltruistSummaryDto
    {
        /// <summary>Leaf <c>altruist*</c> configuration entries.</summary>
        public List<ConfigEntryDto> Configs { get; set; } = new();
        /// <summary>Number of distinct listed types.</summary>
        public int ServiceCount { get; set; }
        /// <summary>Listed types by category, then name.</summary>
        public List<ServiceInfoDto> Services { get; set; } = new();
        /// <summary>Engine settings, or null.</summary>
        public EngineInfoDto? Engine { get; set; }
    }

    /// <summary>Response of <c>POST /dashboard/v1/summary/config/update-batch</c>.</summary>
    public sealed class ConfigBatchUpdateResultDto
    {
        /// <summary>Number of keys set.</summary>
        public int Updated { get; set; }
    }

    // ------------------ Endpoint ------------------

    /// <summary>
    /// <c>POST /dashboard/v1/summary/config/update</c> with a <see cref="ConfigEntryDto"/> body: sets one live-editable
    /// key in the in-memory mutable configuration provider (fires the configuration reload token). 200 with
    /// <c>{ updated, value }</c>; 400 when the key is blank or not live-editable; 500 when no mutable provider is present.
    /// The change is not persisted to any file.
    /// </summary>
    /// <param name="dto">Key and new value (<c>null</c> is stored as an empty string).</param>
    [HttpPost("config/update")]
    public ActionResult UpdateConfig([FromBody] ConfigEntryDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Key))
            return BadRequest("Missing key.");

        if (!LiveConfigRegistry.IsLiveConfig(dto.Key))
            return BadRequest($"Config key '{dto.Key}' is not live-editable.");

        var mutableProvider = GetMutableConfigProvider();

        if (mutableProvider is null)
            return StatusCode(500, "Mutable configuration provider not found.");

        // Set new value -> triggers reload token
        mutableProvider.Set(dto.Key, dto.Value ?? "");

        return Ok(new { Updated = dto.Key, Value = DashboardAccess.RedactValue(dto.Key, dto.Value) });
    }

    /// <summary>
    /// <c>POST /dashboard/v1/summary/config/update-batch</c> with a JSON array of <see cref="ConfigEntryDto"/>: validates every
    /// key first (400 if any is blank or not live-editable; nothing is applied), then sets them all. 200 with a
    /// <see cref="ConfigBatchUpdateResultDto"/> (0 for an empty or missing body); 500 when no mutable provider is present.
    /// </summary>
    /// <param name="entries">Entries to set.</param>
    [HttpPost("config/update-batch")]
    public ActionResult<ConfigBatchUpdateResultDto> UpdateConfigBatch([FromBody] List<ConfigEntryDto>? entries)
    {
        if (entries is null || entries.Count == 0)
            return Ok(new ConfigBatchUpdateResultDto { Updated = 0 });

        var mutableProvider = GetMutableConfigProvider();
        if (mutableProvider is null)
            return StatusCode(500, "Mutable configuration provider not found.");

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                return BadRequest("Missing key.");

            if (!LiveConfigRegistry.IsLiveConfig(entry.Key))
                return BadRequest($"Config key '{entry.Key}' is not live-editable.");
        }

        foreach (var entry in entries)
            mutableProvider.Set(entry.Key, entry.Value ?? string.Empty);

        return Ok(new ConfigBatchUpdateResultDto { Updated = entries.Count });
    }

    /// <summary>
    /// <c>GET /dashboard/v1/summary</c>: 200 with an <see cref="AltruistSummaryDto"/>: every leaf configuration key starting
    /// with <c>altruist</c> (live-editable keys first), the registered service types, and engine settings.
    /// </summary>
    /// <remarks>The services are listed from the container's registrations; none is resolved or constructed.</remarks>
    [HttpGet]
    public ActionResult<AltruistSummaryDto> GetSummary()
    {
        var dto = new AltruistSummaryDto
        {
            Configs = GetConfigEntries(),
            Services = GetServiceInfos()
        };

        dto.ServiceCount = dto.Services
            .Select(s => s.FullName)
            .Distinct(StringComparer.Ordinal)
            .Count();

        dto.Engine = BuildEngineInfo(_engineOptions);

        return Ok(dto);
    }

    // ------------------ Config ------------------

    private List<ConfigEntryDto> GetConfigEntries()
    {
        // First, collect all altruist:* entries
        var raw = _configuration
            .AsEnumerable()
            .Where(kv =>
                !string.IsNullOrEmpty(kv.Key) &&
                kv.Key.StartsWith("altruist", StringComparison.OrdinalIgnoreCase))
            .Select(kv => new ConfigEntryDto
            {
                Key = kv.Key,
                Value = DashboardAccess.RedactValue(kv.Key, kv.Value)
            })
            .ToList();

        // We want to hide *parent* nodes that only act as sections and
        // never have a value: e.g. "altruist", "altruist:dashboard", etc.
        // A "parent" is any key that is a prefix of another key (`parent:`)
        // and whose own Value is null.
        var keys = raw.Select(r => r.Key).ToArray();
        var parentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            var prefix = k + ":";

            if (raw[i].Value == null &&
                keys.Any(other =>
                    !string.Equals(other, k, StringComparison.OrdinalIgnoreCase) &&
                    other.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                parentKeys.Add(k);
            }
        }

        var filtered = raw
            .Where(r => !parentKeys.Contains(r.Key))
            .ToList();

        foreach (var entry in filtered)
        {
            entry.Modifiable = LiveConfigRegistry.IsLiveConfig(entry.Key);
        }

        return filtered
            .OrderByDescending(r => r.Modifiable)
            .ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private MutableConfigProvider? GetMutableConfigProvider()
    {
        var provider = _configuration
            .GetType()
            .GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(_configuration) as IEnumerable<IConfigurationProvider>;

        return provider?
            .FirstOrDefault(p => p is MutableConfigProvider) as MutableConfigProvider;
    }

    // ------------------ Engine Info ------------------

    private static EngineInfoDto? BuildEngineInfo(EngineConfigOptions? options)
    {
        if (options is null)
            return null;

        Vector3Dto? gravityDto = null;

        try
        {
            // supports both System.Numerics.Vector3 & custom structs with X/Y/Z
            var grav = options.Gravity;
            gravityDto = new Vector3Dto
            {
                X = GetFieldOrProperty<float>(grav, "X"),
                Y = GetFieldOrProperty<float>(grav, "Y"),
                Z = GetFieldOrProperty<float>(grav, "Z")
            };
        }
        catch
        {
            // ignore gravity if something goes wrong
        }

        return new EngineInfoDto
        {
            Diagnostics = options.Diagnostics,
            FramerateHz = options.EffectiveFramerateHz,
            Unit = options.Unit,
            Throttle = options.Throttle,
            Gravity = gravityDto
        };
    }

    private static T GetFieldOrProperty<T>(object obj, string name)
    {
        var t = obj.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p != null && p.PropertyType == typeof(T))
            return (T)p.GetValue(obj)!;

        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (f != null && f.FieldType == typeof(T))
            return (T)f.GetValue(obj)!;

        throw new InvalidOperationException($"No field or property '{name}' of type {typeof(T).Name} on {t.FullName}.");
    }

    // ------------------ Service Discovery ------------------

    private List<ServiceInfoDto> GetServiceInfos()
    {
        // From the registrations, without resolving anything: resolving would construct every lazy singleton.
        var types = _serviceProvider.GetRegisteredImplementationTypes().Where(t => !t.IsAbstract);

        var services = new List<ServiceInfoDto>();

        foreach (var type in types)
        {
            // [Portal]
            var portalAttrs = type.GetCustomAttributes<PortalAttribute>(inherit: false).ToArray();
            if (portalAttrs.Length > 0)
            {
                foreach (var pa in portalAttrs)
                {
                    services.Add(new ServiceInfoDto
                    {
                        Name = type.Name,
                        FullName = type.FullName ?? type.Name,
                        Assembly = type.Assembly.GetName().Name ?? "unknown",
                        Category = ServiceCategoryDto.Portal,
                        Endpoint = pa.Endpoint,
                        Context = pa.Context
                    });
                }
            }

            // [Service]
            var serviceAttrs = type.GetCustomAttributes<ServiceAttribute>(inherit: false).ToArray();
            if (serviceAttrs.Length > 0)
            {
                foreach (var sa in serviceAttrs)
                {
                    services.Add(new ServiceInfoDto
                    {
                        Name = type.Name,
                        FullName = type.FullName ?? type.Name,
                        Assembly = type.Assembly.GetName().Name ?? "unknown",
                        Category = ServiceCategoryDto.Service,
                        Lifetime = sa.Lifetime.ToString(),
                        ServiceType = sa.ServiceType?.FullName
                    });
                }
            }

            // IServiceFactory (even if it doesn’t have [Service])
            if (typeof(IServiceFactory).IsAssignableFrom(type))
            {
                services.Add(new ServiceInfoDto
                {
                    Name = type.Name,
                    FullName = type.FullName ?? type.Name,
                    Assembly = type.Assembly.GetName().Name ?? "unknown",
                    Category = ServiceCategoryDto.ServiceFactory
                });
            }

            // IAltruistConfiguration (configs)
            if (typeof(IAltruistConfiguration).IsAssignableFrom(type))
            {
                services.Add(new ServiceInfoDto
                {
                    Name = type.Name,
                    FullName = type.FullName ?? type.Name,
                    Assembly = type.Assembly.GetName().Name ?? "unknown",
                    Category = ServiceCategoryDto.ServiceConfiguration
                });
            }
        }

        return services
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
