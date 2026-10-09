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

using Altruist.Security;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Altruist.Dashboard;

/// <summary>
/// The dashboard's API lab (route <c>/dashboard/v1/lab</c>): lists every attribute-routed HTTP action and every
/// registered socket gate with a sample JSON body, and lets the dashboard user invoke them.
/// </summary>
/// <remarks>
/// Only mapped when <c>altruist:dashboard:enabled</c> is <c>true</c> and the <c>Altruist.Dashboard</c> assembly is loaded.
/// No authentication is applied to this controller. Gate invocation runs the handler as any client id the caller
/// names (bypassing the socket handshake), and HTTP invocation sends a server-side request to the host named in the
/// incoming request; treat it as a development tool and never expose it publicly.
/// </remarks>
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

    /// <summary>Creates the controller.</summary>
    /// <param name="actions">MVC action descriptors (source of HTTP actions).</param>
    /// <param name="jsonOptions">Options for sample generation and payload deserialization.</param>
    /// <param name="codecResolver">Optional packet codec; gate invocation is unavailable without it.</param>
    /// <param name="connectionManager">Optional connection manager; gate invocation is unavailable without it.</param>
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

    /// <summary>
    /// <c>GET /dashboard/v1/lab/actions</c>: 200 with an <see cref="ApiLabActionsDto"/>: one entry per HTTP route and
    /// verb (auth-like routes first, then by verb and path), followed by one entry per socket gate. Each entry carries a
    /// generated sample body (nested to depth 3) and whether it requires auth (<c>[Shield]</c> / <c>[Authorize]</c>,
    /// overridden by <c>[AllowAnonymous]</c>).
    /// </summary>
    [HttpGet("actions")]
    public ActionResult<ApiLabActionsDto> GetActions()
    {
        var items = new List<ApiLabActionDto>();
        items.AddRange(GetHttpActions());
        items.AddRange(GetGateActions());

        return Ok(new ApiLabActionsDto
        {
            Actions = items
                .OrderBy(i => i.Kind.Equals("http", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(i => i.Kind.Equals("http", StringComparison.OrdinalIgnoreCase) && IsAuthEndpoint(i) ? 0 : 1)
                .ThenBy(i => HttpMethodRank(i.Method))
                .ThenBy(i => i.Path ?? i.Event, StringComparer.OrdinalIgnoreCase)
                .ToList()
        });
    }

    /// <summary>
    /// <c>POST /dashboard/v1/lab/invoke</c> with an <see cref="ApiLabInvokeRequestDto"/>: 200 with an
    /// <see cref="ApiLabInvokeResultDto"/>, or 400 for an unknown <see cref="ApiLabInvokeRequestDto.Kind"/>.
    /// </summary>
    /// <remarks>
    /// <c>http</c>: sends the request to <c>{scheme}://{host}{path}</c> of the current request with a shared
    /// <see cref="HttpClient"/> (JSON body except for GET/HEAD; no headers or credentials forwarded) and returns the status
    /// and body. <c>gate</c>: encodes <see cref="ApiLabInvokeRequestDto.BodyJson"/> with the active codec and processes it
    /// as a packet for the event from <see cref="ApiLabInvokeRequestDto.ClientId"/> (default <c>dashboard-lab</c>);
    /// failures are reported in the result, not as HTTP errors.
    /// </remarks>
    /// <param name="request">What to invoke.</param>
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
            var auth = ResolveAuthMetadata(
                action.EndpointMetadata,
                action.MethodInfo,
                action.ControllerTypeInfo.AsType());

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
                    SampleJson = sample,
                    RequiresAuth = auth.RequiresAuth,
                    Shield = auth.Name
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
            var auth = ResolveAuthMetadata(Array.Empty<object>(), method, targetType);

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
                SampleJson = bodyType is null ? "" : PrettySample(bodyType),
                RequiresAuth = auth.RequiresAuth,
                Shield = auth.Name
            };
        }
    }

    private static ApiLabAuthMetadata ResolveAuthMetadata(
        IEnumerable<object> endpointMetadata,
        MethodInfo? method,
        Type? declaringType)
    {
        var metadata = endpointMetadata.ToArray();
        var allowAnonymous = metadata.OfType<IAllowAnonymous>().Any() ||
                             method?.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null ||
                             declaringType?.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;
        if (allowAnonymous)
            return ApiLabAuthMetadata.Public;

        var shield = metadata.OfType<ShieldAttribute>().FirstOrDefault() ??
                     method?.GetCustomAttributes(inherit: true).OfType<ShieldAttribute>().FirstOrDefault() ??
                     declaringType?.GetCustomAttributes(inherit: true).OfType<ShieldAttribute>().FirstOrDefault();
        if (shield is not null)
            return new ApiLabAuthMetadata(true, CleanAttributeName(shield.GetType()));

        var authorize = metadata.OfType<IAuthorizeData>().FirstOrDefault() ??
                        method?.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().FirstOrDefault() ??
                        declaringType?.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().FirstOrDefault();
        if (authorize is not null)
            return new ApiLabAuthMetadata(true, CleanAttributeName(authorize.GetType()));

        return ApiLabAuthMetadata.Public;
    }

    private static string CleanAttributeName(Type type)
    {
        var name = type.Name;
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
    }

    private static int HttpMethodRank(string? method)
    {
        return (method ?? string.Empty).ToUpperInvariant() switch
        {
            "GET" => 0,
            "POST" => 1,
            "PUT" => 2,
            "PATCH" => 3,
            "DELETE" => 4,
            "HEAD" => 5,
            "OPTIONS" => 6,
            _ => 99
        };
    }

    private static bool IsAuthEndpoint(ApiLabActionDto action)
    {
        var text = $"{action.Path} {action.Name} {action.Handler}".ToLowerInvariant();
        return text.Contains("auth", StringComparison.Ordinal) ||
               text.Contains("login", StringComparison.Ordinal) ||
               text.Contains("token", StringComparison.Ordinal) ||
               text.Contains("identity", StringComparison.Ordinal);
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

/// <summary>Response of <c>GET /dashboard/v1/lab/actions</c>.</summary>
public sealed class ApiLabActionsDto
{
    /// <summary>HTTP actions first, then gates.</summary>
    public List<ApiLabActionDto> Actions { get; set; } = new();
}

/// <summary>One invokable HTTP action or socket gate listed by the API lab.</summary>
public sealed class ApiLabActionDto
{
    /// <summary>Stable id: <c>http:{verb}:{path}</c> or <c>gate:{event}:{type}:{method}</c>.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary><c>http</c> or <c>gate</c>.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>HTTP verb, or <c>GATE</c>.</summary>
    public string Method { get; set; } = string.Empty;
    /// <summary>HTTP route template, or the portal route of a gate.</summary>
    public string? Path { get; set; }
    /// <summary>Gate event name (gates only).</summary>
    public string? Event { get; set; }
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary><c>Type.Method</c> that handles it.</summary>
    public string Handler { get; set; } = string.Empty;
    /// <summary>Full name of the body/packet type, if any.</summary>
    public string? PayloadType { get; set; }
    /// <summary>Indented sample JSON body (empty when there is no body).</summary>
    public string SampleJson { get; set; } = string.Empty;
    /// <summary>Whether a <c>[Shield]</c> or <c>[Authorize]</c> attribute applies (and no <c>[AllowAnonymous]</c>).</summary>
    public bool RequiresAuth { get; set; }
    /// <summary>Name of the auth attribute without the <c>Attribute</c> suffix, if any.</summary>
    public string? Shield { get; set; }
}

/// <summary>Body of <c>POST /dashboard/v1/lab/invoke</c>.</summary>
public sealed class ApiLabInvokeRequestDto
{
    /// <summary><c>http</c> or <c>gate</c>.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>HTTP verb (default GET); ignored for gates.</summary>
    public string? Method { get; set; }
    /// <summary>HTTP path, or the portal route passed to the gate.</summary>
    public string? Path { get; set; }
    /// <summary>Gate event name (required for gates).</summary>
    public string? Event { get; set; }
    /// <summary>Client id the gate runs as (default <c>dashboard-lab</c>).</summary>
    public string? ClientId { get; set; }
    /// <summary>JSON request body or packet payload (default <c>{}</c> for gates).</summary>
    public string? BodyJson { get; set; }
}

/// <summary>Result of <c>POST /dashboard/v1/lab/invoke</c>.</summary>
public sealed class ApiLabInvokeResultDto
{
    /// <summary>HTTP 2xx, or the gate returned true.</summary>
    public bool Success { get; set; }
    /// <summary>HTTP status (HTTP invocations that reached the server).</summary>
    public int? StatusCode { get; set; }
    /// <summary>Reason phrase, gate outcome or error message.</summary>
    public string? Message { get; set; }
    /// <summary>HTTP response body text.</summary>
    public string? ResponseBody { get; set; }
}

internal sealed record ApiLabAuthMetadata(bool RequiresAuth, string? Name)
{
    public static ApiLabAuthMetadata Public { get; } = new(false, null);
}
