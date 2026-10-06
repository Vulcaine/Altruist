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
    public const string ConfigPath = "altruist:security:tickets";

    public string QueryParam { get; set; } = "ticket";
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxPerPrincipal { get; set; } = 4;
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);
    public bool RedactRequestLogs { get; set; } = true;

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
public interface IConnectionTicketService
{
    ConnectionTicketOptions Options { get; }

    /// <param name="groupKey">Exposed as <see cref="AuthDetails.GroupKey"/> on the connection (default: the principal id).</param>
    Task<ConnectionTicket> IssueAsync(string principalId, string? groupKey = null, CancellationToken ct = default);

    /// <summary>The ticket's identity, once: null for unknown, expired, malformed or already redeemed tickets.</summary>
    Task<ConnectionTicketIdentity?> RedeemAsync(string? ticket, CancellationToken ct = default);
}

[Service(typeof(IConnectionTicketService))]
public sealed class ConnectionTicketService : IConnectionTicketService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IFleetBackplane _store;

    [ActivatorUtilitiesConstructor]
    public ConnectionTicketService(IFleetBackplane store)
        : this(store, ConnectionTicketOptions.FromConfiguration(AppConfigLoader.Load())) { }

    public ConnectionTicketService(IFleetBackplane store, ConnectionTicketOptions? options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Options = options ?? new ConnectionTicketOptions();
    }

    public ConnectionTicketOptions Options { get; }

    private static string TicketKey(string hash) => "altruist:ticket:" + hash;
    private static string PrincipalKey(string principalId) => "altruist:tickets-of:" + principalId;

    public async Task<ConnectionTicket> IssueAsync(string principalId, string? groupKey = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(principalId))
            throw new ArgumentException("A ticket needs a principal.", nameof(principalId));
        var ticket = OpaqueToken.New();
        var hash = OpaqueToken.Hash(ticket);
        var identity = new ConnectionTicketIdentity(principalId, string.IsNullOrEmpty(groupKey) ? principalId : groupKey);
        await _store.SetAsync(TicketKey(hash), JsonSerializer.Serialize(identity, Json), Options.Lifetime, ct).ConfigureAwait(false);

        // Keep the newest MaxPerPrincipal outstanding; drop the older ones.
        if (Options.MaxPerPrincipal > 0)
        {
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
        return new ConnectionTicket(ticket, (int)Options.Lifetime.TotalSeconds);
    }

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
    public TicketShieldAttribute() : base(typeof(TicketShieldAuth)) { }
}

[Service]
public sealed class TicketShieldAuth : IShieldAuth
{
    private readonly IConnectionTicketService _tickets;

    public TicketShieldAuth(IConnectionTicketService tickets) => _tickets = tickets;

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
    public const string HostingDiagnostics = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public bool IsConfigured { get; set; }

    public Task Configure(IServiceCollection services)
    {
        if (ConnectionTicketOptions.FromConfiguration(AppConfigLoader.Load()).RedactRequestLogs && TicketShieldInUse())
            Redact(services);
        IsConfigured = true;
        return Task.CompletedTask;
    }

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
