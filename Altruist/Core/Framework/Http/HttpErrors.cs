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
/// <remarks>
/// Throw it from controllers for expected failures (validation beyond model binding, conflicts, domain rules) instead of
/// building error results by hand. Only mapped when <c>format: simple</c>; with the default <c>none</c> no filter handles
/// it and ASP.NET answers 500. Use the <see cref="HttpErrorCodes"/> constants where one fits.
/// </remarks>
/// <example>
/// <code>
/// throw new HttpApiException(StatusCodes.Status409Conflict, "name_taken", "That name is already in use.", field: "displayName");
/// </code>
/// </example>
public class HttpApiException : Exception
{
    /// <summary>HTTP status code to answer with.</summary>
    public int Status { get; }

    /// <summary>Machine-readable code, e.g. "invalid_request".</summary>
    public string Code { get; }

    /// <summary>The request field the error is about (camelCase), or null.</summary>
    public string? Field { get; }

    /// <summary>Value for the <c>Retry-After</c> header in seconds, or null for none.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="code">Machine-readable error code (snake_case by convention).</param>
    /// <param name="message">Client-facing text.</param>
    /// <param name="field">The request field the error is about (camelCase), or null.</param>
    /// <param name="retryAfterSeconds"><c>Retry-After</c> seconds, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is null.</exception>
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
/// <param name="Error">Machine-readable code (see <see cref="HttpErrorCodes"/>).</param>
/// <param name="Message">Client-facing text.</param>
/// <param name="Field">Offending request field in camelCase; omitted from JSON when null.</param>
public sealed record HttpError(
    string Error,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);

/// <summary>Error codes Altruist itself answers with.</summary>
public static class HttpErrorCodes
{
    /// <summary>400: malformed request or invalid model.</summary>
    public const string InvalidRequest = "invalid_request";
    /// <summary>401: missing or invalid credentials.</summary>
    public const string Unauthorized = "unauthorized";
    /// <summary>403: authenticated but not allowed.</summary>
    public const string Forbidden = "forbidden";
    /// <summary>429: a rate limit was hit.</summary>
    public const string RateLimited = "rate_limited";
    /// <summary>413: request body over the limit.</summary>
    public const string PayloadTooLarge = "payload_too_large";
    /// <summary>500: unhandled server error (details only in the log).</summary>
    public const string Internal = "internal";
}

/// <summary>How HTTP API failures are answered (<c>altruist:server:http:errors:format</c>).</summary>
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
/// <example>
/// <code>
/// altruist:
///   server:
///     http:
///       errors:
///         format: simple
///         messages:
///           rate-limited: "Slow down a little."   # dashes become underscores: rate_limited
/// </code>
/// </example>
public sealed class HttpErrorOptions
{
    /// <summary>Config section: <c>altruist:server:http:errors</c>.</summary>
    public const string ConfigPath = "altruist:server:http:errors";

    /// <summary><c>format</c>: <see cref="HttpErrorFormat.None"/> (default) or <see cref="HttpErrorFormat.Simple"/>.</summary>
    public HttpErrorFormat Format { get; set; } = HttpErrorFormat.None;

    /// <summary>Client-facing text per error code; defaults for <see cref="HttpErrorCodes"/>, overridden or extended by <c>messages</c>.</summary>
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
    /// <param name="code">Error code.</param>
    public string Message(string code) => Messages.TryGetValue(code, out var m) ? m : code;

    /// <summary>Reads <c>altruist:server:http:errors</c>; message keys may use dashes (converted to underscores).</summary>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="ArgumentException"><c>format</c> is neither <c>none</c> nor <c>simple</c>.</exception>
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
/// <remarks>Singleton registered by <see cref="HttpApiConfiguration"/>. Inject it in controllers, filters or middleware so
/// framework-shaped errors stay consistent; from a controller throwing <see cref="HttpApiException"/> is usually simpler.</remarks>
public sealed class HttpErrorWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Creates a writer.</summary>
    /// <param name="options">Error format and messages.</param>
    public HttpErrorWriter(HttpErrorOptions options) => Options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Error format and messages.</summary>
    public HttpErrorOptions Options { get; }

    /// <summary>Builds an error body; <paramref name="message"/> defaults to the configured text for <paramref name="code"/>.</summary>
    /// <param name="code">Error code.</param>
    /// <param name="message">Client-facing text, or null for the configured one.</param>
    /// <param name="field">Offending field (camelCase), or null.</param>
    public HttpError Error(string code, string? message = null, string? field = null) => new(code, message ?? Options.Message(code), field);

    /// <summary>The error as an MVC result: the body in <c>simple</c> format, the bare status otherwise.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="code">Error code.</param>
    /// <param name="message">Client-facing text, or null for the configured one.</param>
    /// <param name="field">Offending field (camelCase), or null.</param>
    public IActionResult Result(int status, string code, string? message = null, string? field = null) =>
        Options.Format == HttpErrorFormat.Simple
            ? new ObjectResult(Error(code, message, field)) { StatusCode = status }
            : new StatusCodeResult(status);

    /// <summary>Writes the error straight to the response (middleware); nothing when the response has started.</summary>
    /// <remarks>Sets the status in both formats; writes a JSON body only in <c>simple</c> format. Does not set <c>Retry-After</c>
    /// (call <see cref="SetRetryAfter"/> first).</remarks>
    /// <param name="response">The response to write.</param>
    /// <param name="status">HTTP status code.</param>
    /// <param name="code">Error code.</param>
    /// <param name="message">Client-facing text, or null for the configured one.</param>
    /// <param name="field">Offending field (camelCase), or null.</param>
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

    /// <summary>Sets the <c>Retry-After</c> header.</summary>
    /// <param name="response">The response.</param>
    /// <param name="seconds">Delay in whole seconds.</param>
    public static void SetRetryAfter(HttpResponse response, int seconds) =>
        response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Maps exceptions from API controllers to <see cref="HttpError"/> responses (<c>simple</c> format); never leaks internals.</summary>
/// <remarks>Registered as a global MVC filter by <see cref="HttpApiConfiguration"/> only when <c>format: simple</c>. It handles exceptions from
/// MVC actions only; exceptions in middleware are not covered. Every exception is marked handled.</remarks>
public sealed class HttpApiExceptionFilter : IAsyncExceptionFilter
{
    private readonly HttpErrorWriter _errors;
    private readonly ILogger<HttpApiExceptionFilter> _log;

    /// <summary>Created by MVC.</summary>
    /// <param name="errors">Error writer.</param>
    /// <param name="loggerFactory">Logger factory (unexpected exceptions are logged as errors).</param>
    public HttpApiExceptionFilter(HttpErrorWriter errors, ILoggerFactory loggerFactory)
    {
        _errors = errors;
        _log = loggerFactory.CreateLogger<HttpApiExceptionFilter>();
    }

    /// <inheritdoc/>
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
    /// <remarks>Installed as <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c> when <c>format: simple</c>.</remarks>
    /// <param name="context">The action context with the invalid model state.</param>
    /// <param name="errors">Error writer.</param>
    public static IActionResult InvalidModel(ActionContext context, HttpErrorWriter errors)
    {
        var field = context.ModelState.FirstOrDefault(kv => kv.Value?.Errors.Count > 0).Key;
        return new BadRequestObjectResult(errors.Error(HttpErrorCodes.InvalidRequest, null,
            string.IsNullOrEmpty(field) || field.StartsWith('$') ? null : JsonNamingPolicy.CamelCase.ConvertName(field)));
    }
}

/// <summary>JWT bearer 401 / 403 bodies in the <c>simple</c> format (registered unless the app has its own writer).</summary>
/// <remarks>Registered with <c>TryAddSingleton</c> when <c>format: simple</c>; register your own <see cref="IAuthChallengeWriter"/> to replace it.</remarks>
public sealed class HttpErrorChallengeWriter : IAuthChallengeWriter
{
    private readonly HttpErrorWriter _errors;

    /// <summary>Creates the writer.</summary>
    /// <param name="errors">Error writer.</param>
    public HttpErrorChallengeWriter(HttpErrorWriter errors) => _errors = errors;

    /// <inheritdoc/>
    /// <remarks>403 maps to <c>forbidden</c>; any other status is answered as 401 <c>unauthorized</c>.</remarks>
    public Task WriteAsync(HttpContext context, int statusCode) => statusCode == StatusCodes.Status403Forbidden
        ? _errors.WriteAsync(context.Response, StatusCodes.Status403Forbidden, HttpErrorCodes.Forbidden)
        : _errors.WriteAsync(context.Response, StatusCodes.Status401Unauthorized, HttpErrorCodes.Unauthorized);
}
