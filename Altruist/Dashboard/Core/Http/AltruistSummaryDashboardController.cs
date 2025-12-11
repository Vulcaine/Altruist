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
using Microsoft.Extensions.Options;

namespace Altruist.Dashboard;

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

    public AltruistSummaryDashboardController(
        IConfiguration configuration,
        IOptions<EngineConfigOptions> engineOptions,
        JsonSerializerOptions jsonOptions,
        IServiceProvider serviceProvider)
    {
        _configuration = configuration;
        _engineOptions = engineOptions.Value;
        _jsonOptions = jsonOptions;
        _serviceProvider = serviceProvider;
    }

    // ------------------ DTOs ------------------

    public sealed class ConfigEntryDto
    {
        public string Key { get; set; } = default!;
        public string? Value { get; set; }
        public bool Modifiable { get; set; }
    }

    public sealed class Vector3Dto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    public sealed class EngineInfoDto
    {
        public bool Diagnostics { get; set; }
        public int FramerateHz { get; set; }
        public string Unit { get; set; } = "Ticks";
        public int? Throttle { get; set; }
        public Vector3Dto? Gravity { get; set; }
    }

    public enum ServiceCategoryDto
    {
        Portal,
        Service,
        ServiceFactory,
        ServiceConfiguration
    }

    public sealed class ServiceInfoDto
    {
        public string Name { get; set; } = default!;
        public string FullName { get; set; } = default!;
        public string Assembly { get; set; } = default!;
        public ServiceCategoryDto Category { get; set; }

        // Only for [Service]
        public string? Lifetime { get; set; }
        public string? ServiceType { get; set; }

        // Only for [Portal]
        public string? Endpoint { get; set; }
        public string? Context { get; set; }
    }

    public sealed class AltruistSummaryDto
    {
        public List<ConfigEntryDto> Configs { get; set; } = new();
        public int ServiceCount { get; set; }
        public List<ServiceInfoDto> Services { get; set; } = new();
        public EngineInfoDto? Engine { get; set; }
    }

    // ------------------ Endpoint ------------------

    [HttpPost("config/update")]
    public ActionResult UpdateConfig([FromBody] ConfigEntryDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Key))
            return BadRequest("Missing key.");

        var mutable = _configuration
            .GetChildren()
            .Select(c => c)
            .Where(_ => true);

        var provider = _configuration
            .GetType()
            .GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(_configuration) as IEnumerable<IConfigurationProvider>;

        var mutableProvider = provider?
            .FirstOrDefault(p => p is MutableConfigProvider) as MutableConfigProvider;

        if (mutableProvider is null)
            return StatusCode(500, "Mutable configuration provider not found.");

        // Set new value -> triggers reload token
        mutableProvider.Set(dto.Key, dto.Value ?? "");

        return Ok(new { Updated = dto.Key, Value = dto.Value });
    }

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
                Value = kv.Value
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
            .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var entry in filtered)
        {
            entry.Modifiable = LiveConfigRegistry.IsLiveConfig(entry.Key);
        }

        return filtered;
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
            FramerateHz = options.FramerateHz,
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
        // Use the real DI container instead of pure reflection so that we only see
        // services that are actually registered and discovered by Altruist.
        //
        // Assuming there is an extension method:
        //   IEnumerable<T> GetAll<T>(this IServiceProvider provider)
        // as hinted in your snippet.
        var allServices = _serviceProvider.GetAll<object>();

        // Deduplicate by implementation type
        var types = new HashSet<Type>();
        foreach (var service in allServices)
        {
            if (service is null)
                continue;
            var t = service.GetType();
            if (t.IsAbstract || t.IsGenericTypeDefinition)
                continue;
            types.Add(t);
        }

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
