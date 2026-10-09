/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;
using System.Text.Json;

using Altruist.Contracts;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

/// <summary>
/// <c>altruist:security:tickets</c>: <c>query-param</c> ("ticket"), <c>ttl-seconds</c> (30),
/// <c>max-per-principal</c> (4 outstanding; older ones are dropped), <c>session-hours</c> (12: the
/// <see cref="AuthDetails"/> lifetime of a connection opened with a ticket) and
/// <c>redact-request-logs</c> (true; see <see cref="TicketLogRedactionConfiguration"/>).
/// </summary>
public sealed class ConnectionTicketOptions
{
    /// <summary>Config section of the ticket settings.</summary>
    public const string ConfigPath = "altruist:security:tickets";

    /// <summary>Query parameter that carries the ticket (<c>query-param</c>, default <c>ticket</c>).</summary>
    public string QueryParam { get; set; } = "ticket";
    /// <summary>How long an unredeemed ticket stays valid (<c>ttl-seconds</c>, default 30 seconds). Keep it short: the ticket travels in a URL.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Outstanding tickets kept per principal; issuing more drops the oldest (<c>max-per-principal</c>, default 4; 0 = unlimited).</summary>
    public int MaxPerPrincipal { get; set; } = 4;
    /// <summary><see cref="AuthDetails"/> lifetime of a connection opened with a ticket (<c>session-hours</c>, default 12 hours).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);
    /// <summary>Silence ASP.NET request-start logs that would contain the ticket (<c>redact-request-logs</c>, default true).</summary>
    public bool RedactRequestLogs { get; set; } = true;

    /// <summary>Reads <see cref="ConfigPath"/>; missing keys keep their defaults.</summary>
    public static ConnectionTicketOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new ConnectionTicketOptions();
        options.QueryParam = TokenConfig.Text(section, "query-param") ?? options.QueryParam;
        if (TokenConfig.Number(section, "ttl-seconds") is { } ttl)
            options.Lifetime = TimeSpan.FromSeconds(ttl);
        if (TokenConfig.Number(section, "max-per-principal") is { } max)
            options.MaxPerPrincipal = (int)max;
        if (TokenConfig.Number(section, "session-hours") is { } hours)
            options.SessionLifetime = TimeSpan.FromHours(hours);
        if (bool.TryParse(TokenConfig.Text(section, "redact-request-logs"), out var redact))
            options.RedactRequestLogs = redact;
        return options;
    }
}

/// <summary>A one-time ticket for the client to connect with (<c>wss://host/portal?ticket=...</c>).</summary>
public sealed record ConnectionTicket(string Ticket, int ExpiresInSeconds);

/// <summary>Who a redeemed ticket was issued to.</summary>
public sealed record ConnectionTicketIdentity(string PrincipalId, string GroupKey);

/// <summary>
/// One-time connection tickets. Browsers cannot set an Authorization header on a WebSocket, so the
/// client trades its access token (an authenticated HTTP call of yours) for a short-lived ticket
/// and puts it in the connection URL; <see cref="TicketShieldAttribute"/> redeems it on the upgrade.
/// Tickets are 256-bit random, stored hashed in the <see cref="IFleetBackplane"/> (shared by every
/// server of a fleet, so a ticket from one server works on any other) and consumed atomically on
/// first use.
/// </summary>
/// <remarks>
/// Use tickets for WebSocket (or other URL-only) connections; for ordinary HTTP calls send the access token
/// from <see cref="IAccessTokenIssuer"/> with <see cref="JwtShieldAttribute"/>. Tickets live
/// <see cref="ConnectionTicketOptions.Lifetime"/> (30 seconds by default), so request one right before connecting.
/// </remarks>
/// <example>
/// <code>
/// [JwtShield, HttpPost("ticket")]
/// public async Task&lt;IActionResult&gt; Ticket() =&gt; Ok(await tickets.IssueAsync(User.PrincipalId()!));
///
/// [TicketShield, Portal("/game")]
/// public class GamePortal : Portal { }   // client connects to wss://host/game?ticket=...
/// </code>
/// </example>
public interface IConnectionTicketService
{
    /// <summary>The settings in effect.</summary>
    ConnectionTicketOptions Options { get; }

    /// <summary>
    /// Issues a single-use ticket for <paramref name="principalId"/>, valid <see cref="ConnectionTicketOptions.Lifetime"/>.
    /// Call it from an authenticated endpoint; the raw ticket is returned once and stored only hashed.
    /// </summary>
    /// <param name="principalId">Who the connection will belong to.</param>
    /// <param name="groupKey">Exposed as <see cref="AuthDetails.GroupKey"/> on the connection (default: the principal id).</param>
    /// <param name="ct">Cancels the backplane calls.</param>
    Task<ConnectionTicket> IssueAsync(string principalId, string? groupKey = null, CancellationToken ct = default);

    /// <summary>The ticket's identity, once: null for unknown, expired, malformed or already redeemed tickets.</summary>
    Task<ConnectionTicketIdentity?> RedeemAsync(string? ticket, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IConnectionTicketService"/> (singleton) over the <see cref="IFleetBackplane"/>: keys
/// <c>altruist:ticket:{hash}</c> hold the identity and <c>altruist:tickets-of:{principal}</c> the outstanding list;
/// <c>altruist:tickets-lock:{principal}</c> serializes issues for one principal across the fleet, so
/// <see cref="ConnectionTicketOptions.MaxPerPrincipal"/> holds under concurrency.
/// </summary>
[Service(typeof(IConnectionTicketService))]
public sealed class ConnectionTicketService : IConnectionTicketService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IFleetBackplane _store;

    /// <summary>DI constructor: options from <see cref="ConnectionTicketOptions.ConfigPath"/>.</summary>
    [ActivatorUtilitiesConstructor]
    public ConnectionTicketService(IFleetBackplane store)
        : this(store, ConnectionTicketOptions.FromConfiguration(AppConfigLoader.Load())) { }

    /// <summary>Creates the service with explicit options (tests).</summary>
    /// <param name="store">Where tickets are kept.</param>
    /// <param name="options">Settings (defaults when null).</param>
    public ConnectionTicketService(IFleetBackplane store, ConnectionTicketOptions? options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Options = options ?? new ConnectionTicketOptions();
    }

    /// <inheritdoc/>
    public ConnectionTicketOptions Options { get; }

    private static string TicketKey(string hash) => "altruist:ticket:" + hash;
    private static string PrincipalKey(string principalId) => "altruist:tickets-of:" + principalId;
    private static string LockKey(string principalId) => "altruist:tickets-lock:" + principalId;

    // Held only for a few backplane round trips; expires on its own if the holder dies mid-issue.
    private static readonly TimeSpan IssueLockTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IssueLockRetry = TimeSpan.FromMilliseconds(5);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">When <paramref name="principalId"/> is empty.</exception>
    public async Task<ConnectionTicket> IssueAsync(string principalId, string? groupKey = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(principalId))
            throw new ArgumentException("A ticket needs a principal.", nameof(principalId));
        var ticket = OpaqueToken.New();
        var hash = OpaqueToken.Hash(ticket);
        var identity = new ConnectionTicketIdentity(principalId, string.IsNullOrEmpty(groupKey) ? principalId : groupKey);

        if (Options.MaxPerPrincipal <= 0)
        {
            await _store.SetAsync(TicketKey(hash), JsonSerializer.Serialize(identity, Json), Options.Lifetime, ct).ConfigureAwait(false);
            return new ConnectionTicket(ticket, (int)Options.Lifetime.TotalSeconds);
        }

        // The outstanding list is read, changed and written back: concurrent issues for one principal (on any
        // server) must not interleave, or each keeps its own copy and more than MaxPerPrincipal tickets survive.
        var lockKey = LockKey(principalId);
        var lockOwner = OpaqueToken.New();
        while (!await _store.SetIfAbsentAsync(lockKey, lockOwner, IssueLockTtl, ct).ConfigureAwait(false))
            await Task.Delay(IssueLockRetry, ct).ConfigureAwait(false);
        try
        {
            await _store.SetAsync(TicketKey(hash), JsonSerializer.Serialize(identity, Json), Options.Lifetime, ct).ConfigureAwait(false);
            var raw = await _store.GetAsync(PrincipalKey(principalId), ct).ConfigureAwait(false);
            var outstanding = raw is null ? new List<string>() : JsonSerializer.Deserialize<List<string>>(raw, Json) ?? new List<string>();
            outstanding.Add(hash);
            while (outstanding.Count > Options.MaxPerPrincipal)
            {
                await _store.DeleteAsync(TicketKey(outstanding[0]), ct).ConfigureAwait(false);
                outstanding.RemoveAt(0);
            }
            await _store.SetAsync(PrincipalKey(principalId), JsonSerializer.Serialize(outstanding, Json), Options.Lifetime, ct).ConfigureAwait(false);
        }
        finally
        {
            await _store.DeleteIfValueAsync(lockKey, lockOwner, CancellationToken.None).ConfigureAwait(false);
        }
        return new ConnectionTicket(ticket, (int)Options.Lifetime.TotalSeconds);
    }

    /// <inheritdoc/>
    public async Task<ConnectionTicketIdentity?> RedeemAsync(string? ticket, CancellationToken ct = default)
    {
        if (!OpaqueToken.IsWellFormed(ticket))
            return null;
        // Take is atomic: two concurrent connects (on any servers) cannot both succeed.
        var raw = await _store.TakeAsync(TicketKey(OpaqueToken.Hash(ticket!)), ct).ConfigureAwait(false);
        return raw is null ? null : JsonSerializer.Deserialize<ConnectionTicketIdentity>(raw, Json);
    }
}

/// <summary>
/// Shield for a portal (or controller) opened with a connection ticket: the request is rejected
/// with 401 unless <c>?{query-param}=</c> holds a valid ticket from <see cref="IConnectionTicketService"/>.
/// On success the connection's <see cref="AuthDetails.PrincipalId"/> and <see cref="AuthDetails.GroupKey"/>
/// are the ticket's.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class TicketShieldAttribute : ShieldAttribute
{
    /// <summary>Creates the shield.</summary>
    public TicketShieldAttribute() : base(typeof(TicketShieldAuth)) { }
}

/// <summary>The <see cref="IShieldAuth"/> behind <see cref="TicketShieldAttribute"/>: redeems the query-string ticket of an <see cref="HttpAuthContext"/> (other contexts fail).</summary>
[Service]
public sealed class TicketShieldAuth : IShieldAuth
{
    private readonly IConnectionTicketService _tickets;

    /// <summary>Creates the handler (resolved by DI).</summary>
    public TicketShieldAuth(IConnectionTicketService tickets) => _tickets = tickets;

    /// <summary>Redeems the ticket; on success returns details with the ticket's principal and group key, the client IP and <see cref="ConnectionTicketOptions.SessionLifetime"/>.</summary>
    public async Task<AuthResult> HandleAuthAsync(IAuthContext context)
    {
        if (context is not HttpAuthContext http)
            return new AuthResult(AuthorizationResult.Failed(), null);

        var ticket = http.HttpContext.Request.Query[_tickets.Options.QueryParam].ToString();
        var identity = await _tickets.RedeemAsync(ticket, http.HttpContext.RequestAborted);
        if (identity is null)
            return new AuthResult(AuthorizationResult.Failed(), null);

        // Token is left empty: nothing downstream needs the ticket once it is consumed.
        var details = new AuthDetails("", identity.PrincipalId, http.HttpContext.ClientIp(), identity.GroupKey,
            _tickets.Options.SessionLifetime);
        return new AuthResult(AuthorizationResult.Success(), details);
    }
}

/// <summary>
/// ASP.NET's request log lines ("Request starting GET /game?ticket=...") contain the full URL, so
/// the one-time ticket secret. When the application uses <see cref="TicketShieldAttribute"/> and
/// <c>altruist:security:tickets:redact-request-logs</c> is true (default), only warnings of that
/// log category (<c>Microsoft.AspNetCore.Hosting.Diagnostics</c>) are kept.
/// </summary>
[ServiceConfiguration]
public sealed class TicketLogRedactionConfiguration : IAltruistConfiguration
{
    /// <summary>The ASP.NET log category whose request lines contain the full URL.</summary>
    public const string HostingDiagnostics = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>True once <see cref="Configure"/> ran.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Adds the log filter when redaction is enabled and some class or method carries <see cref="TicketShieldAttribute"/>. Called by Altruist at start-up.</summary>
    public Task Configure(IServiceCollection services)
    {
        if (ConnectionTicketOptions.FromConfiguration(AppConfigLoader.Load()).RedactRequestLogs && TicketShieldInUse())
            Redact(services);
        IsConfigured = true;
        return Task.CompletedTask;
    }

    /// <summary>Adds a filter keeping only warnings and above from <see cref="HostingDiagnostics"/>.</summary>
    public static void Redact(IServiceCollection services) =>
        services.Configure<LoggerFilterOptions>(o =>
            o.Rules.Add(new LoggerFilterRule(null, HostingDiagnostics, LogLevel.Warning, null)));

    private static bool TicketShieldInUse() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .SelectMany(TypeDiscovery.SafeGetTypes)
            .Where(t => t is { IsClass: true })
            .Any(t => t.IsDefined(typeof(TicketShieldAttribute), inherit: true) ||
                      t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                          .Any(m => m.IsDefined(typeof(TicketShieldAttribute), inherit: true)));
}
