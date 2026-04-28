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

using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Altruist.Dashboard;

[ApiController]
[Route("/dashboard/v1/lab")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class ApiLabDashboardController : ControllerBase
{
    private static readonly HttpClient Http = new();
    private readonly IActionDescriptorCollectionProvider _actions;
    private readonly ICodecResolver? _codecResolver;
    private readonly IConnectionManager? _connectionManager;
    private readonly JsonSerializerOptions _jsonOptions;

    public ApiLabDashboardController(
        IActionDescriptorCollectionProvider actions,
        JsonSerializerOptions jsonOptions,
        ICodecResolver? codecResolver = null,
        IConnectionManager? connectionManager = null)
    {
        _actions = actions;
        _jsonOptions = jsonOptions;
        _codecResolver = codecResolver;
        _connectionManager = connectionManager;
    }

    [HttpGet("actions")]
    public ActionResult<ApiLabActionsDto> GetActions()
    {
        var items = new List<ApiLabActionDto>();
        items.AddRange(GetHttpActions());
        items.AddRange(GetGateActions());

        return Ok(new ApiLabActionsDto
        {
            Actions = items
                .OrderBy(i => i.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Path ?? i.Event, StringComparer.OrdinalIgnoreCase)
                .ToList()
        });
    }

    [HttpPost("invoke")]
    public async Task<ActionResult<ApiLabInvokeResultDto>> Invoke([FromBody] ApiLabInvokeRequestDto request)
    {
        if (request.Kind.Equals("gate", StringComparison.OrdinalIgnoreCase))
            return Ok(await InvokeGate(request));

        if (request.Kind.Equals("http", StringComparison.OrdinalIgnoreCase))
            return Ok(await InvokeHttp(request));

        return BadRequest("Unknown lab action kind.");
    }

    private IEnumerable<ApiLabActionDto> GetHttpActions()
    {
        foreach (var action in _actions.ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            var route = "/" + (action.AttributeRouteInfo?.Template ?? string.Empty).TrimStart('/');
            if (string.IsNullOrWhiteSpace(route) || route == "/")
                continue;

            var methods = action.EndpointMetadata
                .OfType<HttpMethodMetadata>()
                .SelectMany(m => m.HttpMethods)
                .DefaultIfEmpty("GET")
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var bodyType = FindHttpBodyType(action);
            var sample = bodyType is null ? "" : PrettySample(bodyType);

            foreach (var method in methods)
            {
                yield return new ApiLabActionDto
                {
                    Id = $"http:{method}:{route}",
                    Kind = "http",
                    Method = method,
                    Path = route,
                    Name = $"{method} {route}",
                    Handler = $"{action.ControllerTypeInfo.Name}.{action.MethodInfo.Name}",
                    PayloadType = bodyType?.FullName,
                    SampleJson = sample
                };
            }
        }
    }

    private IEnumerable<ApiLabActionDto> GetGateActions()
    {
        foreach (var (eventName, handler) in PortalGateRegistry<IPortal>.GetAllHandlerEntries())
        {
            var method = handler.Method;
            var targetType = handler.Target?.GetType() ?? method.DeclaringType;
            var portal = handler.Target as IPortal;
            var parameters = method.GetParameters();
            var bodyType = parameters.Length >= 2 && typeof(IPacket).IsAssignableFrom(parameters[0].ParameterType)
                ? parameters[0].ParameterType
                : null;

            yield return new ApiLabActionDto
            {
                Id = $"gate:{eventName}:{targetType?.FullName}:{method.Name}",
                Kind = "gate",
                Method = "GATE",
                Path = portal?.Route,
                Event = eventName,
                Name = eventName,
                Handler = $"{targetType?.Name ?? "Portal"}.{method.Name}",
                PayloadType = bodyType?.FullName,
                SampleJson = bodyType is null ? "" : PrettySample(bodyType)
            };
        }
    }

    private async Task<ApiLabInvokeResultDto> InvokeHttp(ApiLabInvokeRequestDto request)
    {
        var method = new HttpMethod(string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method);
        var path = "/" + (request.Path ?? string.Empty).TrimStart('/');
        var url = $"{Request.Scheme}://{Request.Host}{path}";
        var message = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(request.BodyJson) &&
            method != HttpMethod.Get &&
            method != HttpMethod.Head)
        {
            message.Content = new StringContent(request.BodyJson, Encoding.UTF8, "application/json");
        }

        try
        {
            var response = await Http.SendAsync(message);
            var body = await response.Content.ReadAsStringAsync();
            return new ApiLabInvokeResultDto
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                Message = response.ReasonPhrase,
                ResponseBody = body
            };
        }
        catch (Exception ex)
        {
            return new ApiLabInvokeResultDto
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    private async Task<ApiLabInvokeResultDto> InvokeGate(ApiLabInvokeRequestDto request)
    {
        if (_connectionManager is null || _codecResolver is null)
        {
            return new ApiLabInvokeResultDto
            {
                Success = false,
                Message = "Transport services are not available."
            };
        }

        if (string.IsNullOrWhiteSpace(request.Event))
        {
            return new ApiLabInvokeResultDto
            {
                Success = false,
                Message = "Missing gate event."
            };
        }

        if (!PortalGateRegistry<IPortal>.TryGetHandler(request.Event, out var handler))
        {
            return new ApiLabInvokeResultDto
            {
                Success = false,
                Message = $"Gate '{request.Event}' was not found."
            };
        }

        try
        {
            var payloadBytes = BuildGatePayload(handler, request.BodyJson);
            var clientId = string.IsNullOrWhiteSpace(request.ClientId)
                ? "dashboard-lab"
                : request.ClientId.Trim();
            var ok = await _connectionManager.ProcessPacket(
                new AltruistPacket(request.Event),
                payloadBytes,
                request.Path ?? string.Empty,
                clientId);

            return new ApiLabInvokeResultDto
            {
                Success = ok,
                Message = ok
                    ? "Gate executed. Socket-side effects are visible in Network."
                    : "Gate returned false."
            };
        }
        catch (Exception ex)
        {
            return new ApiLabInvokeResultDto
            {
                Success = false,
                Message = ex.InnerException?.Message ?? ex.Message
            };
        }
    }

    private byte[] BuildGatePayload(Delegate handler, string? bodyJson)
    {
        var parameters = handler.Method.GetParameters();
        if (parameters.Length < 2 || !typeof(IPacket).IsAssignableFrom(parameters[0].ParameterType))
            return Array.Empty<byte>();

        var payloadType = parameters[0].ParameterType;
        var json = string.IsNullOrWhiteSpace(bodyJson) ? "{}" : bodyJson;
        var codec = _codecResolver!.Resolve();

        if (codec.GetType().Name.Contains("Json", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetBytes(json);

        var payload = JsonSerializer.Deserialize(json, payloadType, _jsonOptions)
            ?? Activator.CreateInstance(payloadType);
        if (payload is null)
            return Array.Empty<byte>();

        return codec.Encoder.Encode(payload, payloadType);
    }

    private static Type? FindHttpBodyType(ControllerActionDescriptor action)
    {
        return action.Parameters
            .OfType<ControllerParameterDescriptor>()
            .Select(p => p.ParameterInfo)
            .FirstOrDefault(IsBodyParameter)
            ?.ParameterType;
    }

    private static bool IsBodyParameter(ParameterInfo parameter)
    {
        if (parameter.GetCustomAttribute<FromBodyAttribute>() is not null)
            return true;

        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum)
            return false;

        return parameter.GetCustomAttribute<FromRouteAttribute>() is null &&
               parameter.GetCustomAttribute<FromQueryAttribute>() is null &&
               parameter.GetCustomAttribute<FromHeaderAttribute>() is null &&
               parameter.GetCustomAttribute<FromServicesAttribute>() is null;
    }

    private string PrettySample(Type type)
    {
        var sample = BuildSample(type, 0);
        return JsonSerializer.Serialize(sample, new JsonSerializerOptions(_jsonOptions) { WriteIndented = true });
    }

    private static object? BuildSample(Type type, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (depth > 3)
            return null;

        if (type == typeof(string) || type == typeof(Guid))
            return string.Empty;
        if (type == typeof(bool))
            return false;
        if (type.IsEnum)
            return Enum.GetNames(type).FirstOrDefault() ?? string.Empty;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return DateTime.UtcNow;
        if (type.IsPrimitive || type == typeof(decimal))
            return 0;
        if (type.IsArray)
            return Array.CreateInstance(type.GetElementType() ?? typeof(object), 0);
        if (typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type.IsAbstract || type.IsInterface)
            return null;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                continue;
            result[prop.Name] = BuildSample(prop.PropertyType, depth + 1);
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            result[field.Name] = BuildSample(field.FieldType, depth + 1);
        }

        return result;
    }
}

public sealed class ApiLabActionsDto
{
    public List<ApiLabActionDto> Actions { get; set; } = new();
}

public sealed class ApiLabActionDto
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string? Path { get; set; }
    public string? Event { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Handler { get; set; } = string.Empty;
    public string? PayloadType { get; set; }
    public string SampleJson { get; set; } = string.Empty;
}

public sealed class ApiLabInvokeRequestDto
{
    public string Kind { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string? Path { get; set; }
    public string? Event { get; set; }
    public string? ClientId { get; set; }
    public string? BodyJson { get; set; }
}

public sealed class ApiLabInvokeResultDto
{
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string? Message { get; set; }
    public string? ResponseBody { get; set; }
}
