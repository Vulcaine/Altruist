/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Altruist.Security;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Altruist.Http;

/// <summary>
/// An expected, user-facing failure of an HTTP API call. With <c>altruist:server:http:errors:format: simple</c>
/// it is answered with <see cref="Status"/> and the body <c>{error, message, field?}</c> (and a
/// <c>Retry-After</c> header when <see cref="RetryAfterSeconds"/> is set). The message is shown to
/// the client: never put internals in it.
/// </summary>
public class HttpApiException : Exception
{
    public int Status { get; }

    /// <summary>Machine-readable code, e.g. "invalid_request".</summary>
    public string Code { get; }

    /// <summary>The request field the error is about (camelCase), or null.</summary>
    public string? Field { get; }

    public int? RetryAfterSeconds { get; }

    public HttpApiException(int status, string code, string message, string? field = null, int? retryAfterSeconds = null)
        : base(message)
    {
        Status = status;
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Field = field;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

/// <summary>The <c>simple</c> error body: <c>{"error": code, "message": text, "field": name?}</c>.</summary>
public sealed record HttpError(
    string Error,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);

/// <summary>Error codes Altruist itself answers with.</summary>
public static class HttpErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string RateLimited = "rate_limited";
    public const string PayloadTooLarge = "payload_too_large";
    public const string Internal = "internal";
}

public enum HttpErrorFormat
{
    /// <summary>ASP.NET's defaults (problem details for invalid models, unhandled exceptions are 500s).</summary>
    None,

    /// <summary><see cref="HttpError"/> bodies for every failure (see <see cref="HttpErrorOptions"/>).</summary>
    Simple,
}

/// <summary>
/// <c>altruist:server:http:errors</c>: <c>format</c> (<c>none</c>, the default, or <c>simple</c>) and
/// <c>messages</c> (code to text) for the failures Altruist answers itself. With <c>simple</c>:
/// <list type="bullet">
/// <item><see cref="HttpApiException"/> from a controller → its status, code, message, field and <c>Retry-After</c>.</item>
/// <item>An invalid model (<c>[ApiController]</c>) → 400 <c>invalid_request</c> with the first invalid field (camelCase).</item>
/// <item>A body over the server's limit → 413 <c>payload_too_large</c>; another bad request → <c>invalid_request</c>.</item>
/// <item>A request the client aborted → 499 without a body; anything else → 500 <c>internal</c> (logged, never leaked).</item>
/// <item>JWT bearer 401 / 403 → <c>unauthorized</c> / <c>forbidden</c> (unless the app registers its own <see cref="IAuthChallengeWriter"/>).</item>
/// <item>Rate limits and the API body limit (<see cref="HttpHardeningOptions"/>) → 429 <c>rate_limited</c> / 413.</item>
/// </list>
/// </summary>
public sealed class HttpErrorOptions
{
    public const string ConfigPath = "altruist:server:http:errors";

    public HttpErrorFormat Format { get; set; } = HttpErrorFormat.None;

    public Dictionary<string, string> Messages { get; } = new(StringComparer.Ordinal)
    {
        [HttpErrorCodes.InvalidRequest] = "That request was not accepted.",
        [HttpErrorCodes.Unauthorized] = "Please sign in again.",
        [HttpErrorCodes.Forbidden] = "You are not allowed to do that.",
        [HttpErrorCodes.RateLimited] = "Too many requests. Please wait a moment.",
        [HttpErrorCodes.PayloadTooLarge] = "That request is too large.",
        [HttpErrorCodes.Internal] = "The server had a problem. Please try again shortly.",
    };

    /// <summary>The configured text for an error code (the code itself when none is known).</summary>
    public string Message(string code) => Messages.TryGetValue(code, out var m) ? m : code;

    public static HttpErrorOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new HttpErrorOptions();
        options.Format = TokenConfig.Text(section, "format")?.ToLowerInvariant() switch
        {
            null or "none" => HttpErrorFormat.None,
            "simple" => HttpErrorFormat.Simple,
            var other => throw new ArgumentException($"{ConfigPath}:format must be none or simple, not '{other}'."),
        };
        foreach (var message in section.GetSection("messages").GetChildren())
            if (!string.IsNullOrWhiteSpace(message.Value))
                options.Messages[message.Key.Replace('-', '_')] = message.Value;
        return options;
    }
}

/// <summary>Writes error responses in the configured <see cref="HttpErrorFormat"/>.</summary>
public sealed class HttpErrorWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpErrorWriter(HttpErrorOptions options) => Options = options ?? throw new ArgumentNullException(nameof(options));

    public HttpErrorOptions Options { get; }

    public HttpError Error(string code, string? message = null, string? field = null) => new(code, message ?? Options.Message(code), field);

    /// <summary>The error as an MVC result: the body in <c>simple</c> format, the bare status otherwise.</summary>
    public IActionResult Result(int status, string code, string? message = null, string? field = null) =>
        Options.Format == HttpErrorFormat.Simple
            ? new ObjectResult(Error(code, message, field)) { StatusCode = status }
            : new StatusCodeResult(status);

    /// <summary>Writes the error straight to the response (middleware); nothing when the response has started.</summary>
    public Task WriteAsync(HttpResponse response, int status, string code, string? message = null, string? field = null)
    {
        if (response.HasStarted)
            return Task.CompletedTask;
        response.StatusCode = status;
        if (Options.Format != HttpErrorFormat.Simple)
            return Task.CompletedTask;
        response.ContentType = "application/json; charset=utf-8";
        return response.WriteAsync(JsonSerializer.Serialize(Error(code, message, field), Json));
    }

    public static void SetRetryAfter(HttpResponse response, int seconds) =>
        response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Maps exceptions from API controllers to <see cref="HttpError"/> responses (<c>simple</c> format); never leaks internals.</summary>
public sealed class HttpApiExceptionFilter : IAsyncExceptionFilter
{
    private readonly HttpErrorWriter _errors;
    private readonly ILogger<HttpApiExceptionFilter> _log;

    public HttpApiExceptionFilter(HttpErrorWriter errors, ILoggerFactory loggerFactory)
    {
        _errors = errors;
        _log = loggerFactory.CreateLogger<HttpApiExceptionFilter>();
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case HttpApiException api:
                if (api.RetryAfterSeconds is { } retry)
                    HttpErrorWriter.SetRetryAfter(context.HttpContext.Response, retry);
                context.Result = _errors.Result(api.Status, api.Code, api.Message, api.Field);
                break;
            case BadHttpRequestException bad:
                context.Result = bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? _errors.Result(bad.StatusCode, HttpErrorCodes.PayloadTooLarge)
                    : _errors.Result(bad.StatusCode, HttpErrorCodes.InvalidRequest);
                break;
            case OperationCanceledException when context.HttpContext.RequestAborted.IsCancellationRequested:
                context.Result = new StatusCodeResult(499);
                break;
            default:
                _log.LogError(context.Exception, "Unhandled API error on {Method} {Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path);
                context.Result = _errors.Result(StatusCodes.Status500InternalServerError, HttpErrorCodes.Internal);
                break;
        }
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The <c>[ApiController]</c> answer to an invalid model: 400 <c>invalid_request</c> naming the
    /// first invalid field in camelCase (none for body-level errors such as malformed JSON).
    /// </summary>
    public static IActionResult InvalidModel(ActionContext context, HttpErrorWriter errors)
    {
        var field = context.ModelState.FirstOrDefault(kv => kv.Value?.Errors.Count > 0).Key;
        return new BadRequestObjectResult(errors.Error(HttpErrorCodes.InvalidRequest, null,
            string.IsNullOrEmpty(field) || field.StartsWith('$') ? null : JsonNamingPolicy.CamelCase.ConvertName(field)));
    }
}

/// <summary>JWT bearer 401 / 403 bodies in the <c>simple</c> format (registered unless the app has its own writer).</summary>
public sealed class HttpErrorChallengeWriter : IAuthChallengeWriter
{
    private readonly HttpErrorWriter _errors;

    public HttpErrorChallengeWriter(HttpErrorWriter errors) => _errors = errors;

    public Task WriteAsync(HttpContext context, int statusCode) => statusCode == StatusCodes.Status403Forbidden
        ? _errors.WriteAsync(context.Response, StatusCodes.Status403Forbidden, HttpErrorCodes.Forbidden)
        : _errors.WriteAsync(context.Response, StatusCodes.Status401Unauthorized, HttpErrorCodes.Unauthorized);
}
