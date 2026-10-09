/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

/// <summary>
/// Resolves the correct ICodec based on transport mode and config.
/// Resolution order: transport-specific config → global config → <c>messagepack</c>.
///
/// Discovers all [CodecProvider("name")] types at startup and instantiates them on demand.
/// This works independently of ConditionalOnConfig — codecs that aren't the global default
/// can still be used for specific transports.
/// </summary>
/// <remarks>
/// Config keys: <c>altruist:server:transport:&lt;mode&gt;:codec:provider</c> (per transport, e.g. <c>websocket</c>,
/// <c>tcp</c>) and <c>altruist:server:transport:codec:provider</c> (global). Unknown names fall through to the next step
/// <c>tcp</c>) and <c>altruist:server:transport:codec:provider</c> (global, default <c>messagepack</c>). A configured name that no
/// codec provides throws. Registered as a singleton by <see cref="CodecResolver"/> only when <c>altruist:server:transport</c> is
/// </remarks>
/// <example>
/// <code>
/// var codec = codecResolver.ResolveForConnection(connection);
/// var packet = codec.Decoder.Decode&lt;MyPacket&gt;(bytes);
/// </code>
/// </example>
public interface ICodecResolver
{
    /// <summary>
    /// Resolve the codec for a given transport mode.
    /// Pass null for the global default.
    /// </summary>
    /// <param name="transportMode">Transport name as used in config (e.g. <c>websocket</c>, <c>tcp</c>), or <c>null</c>.</param>
    /// <returns>The per-transport codec, else the global one (<c>messagepack</c> when unconfigured).</returns>
    /// <exception cref="InvalidOperationException">A configured provider name has no <see cref="CodecProviderAttribute"/> codec.</exception>
    ICodec Resolve(string? transportMode = null);

    /// <summary>
    /// Resolve the codec based on the connection type (WebSocketConnection → "websocket", etc.).
    /// </summary>
    /// <remarks>The mode is the connection's <see cref="AltruistConnection.TransportMode"/> (<c>websocket</c>, <c>tcp</c>,
    /// <c>udp</c>); connections without one use the global default.</remarks>
    /// <param name="connection">The connection whose transport decides the codec.</param>
    ICodec ResolveForConnection(AltruistConnection connection);

    /// <summary>Get a codec by its provider name directly (case-insensitive), bypassing config. Returns <c>null</c> if unknown.</summary>
    ICodec? GetByName(string providerName);
}

/// <summary>
/// Default <see cref="ICodecResolver"/>: collects DI-registered codecs plus every other
/// <see cref="CodecProviderAttribute"/> type found by reflection (created with <c>ActivatorUtilities</c>) once, at construction.
/// </summary>
[Service(typeof(ICodecResolver))]
[ConditionalOnConfig("altruist:server:transport")]
public class CodecResolver : ICodecResolver
{
    private readonly Dictionary<string, ICodec> _codecs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfiguration _config;
    private readonly ILogger _logger;

    /// <summary>Instantiates every codec provider (reusing the DI-registered global <see cref="ICodec"/>). Constructed by DI.</summary>
    public CodecResolver(IServiceProvider serviceProvider, IConfiguration config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<CodecResolver>();

        // The global codec is a DI singleton: share that instance.
        foreach (var codec in serviceProvider.GetServices<ICodec>())
        {
            var attr = codec.GetType().GetCustomAttribute<CodecProviderAttribute>();
            if (attr != null)
                _codecs[attr.Name] = codec;
        }

        foreach (var (name, type) in CodecProviders.FindTypes())
        {
            if (_codecs.ContainsKey(name))
                continue;
            _codecs[name] = (ICodec)ActivatorUtilities.CreateInstance(serviceProvider, type);
            _logger.LogDebug("Discovered codec provider: {Name} ({Type})", name, type.Name);
        }

        if (_codecs.Count > 0)
            _logger.LogInformation("Available codec providers: {Codecs}", string.Join(", ", _codecs.Keys));
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">A configured provider name has no <see cref="CodecProviderAttribute"/> codec.</exception>
    public ICodec Resolve(string? transportMode = null)
    {
        if (!string.IsNullOrEmpty(transportMode))
        {
            var specific = _config[$"altruist:server:transport:{transportMode}:codec:provider"];
            if (!string.IsNullOrEmpty(specific))
                return Named(specific, $"altruist:server:transport:{transportMode}:codec:provider");
        }

        var global = _config[CodecProviders.GlobalProviderKey];
        return Named(string.IsNullOrEmpty(global) ? CodecProviders.DefaultProvider : global, CodecProviders.GlobalProviderKey);
    }

    private ICodec Named(string provider, string key) =>
        _codecs.TryGetValue(provider, out var codec)
            ? codec
            : throw new InvalidOperationException(
                $"{key} is '{provider}', but no codec has [CodecProvider(\"{provider}\")]. Available: {string.Join(", ", _codecs.Keys)}.");

    /// <inheritdoc/>
    public ICodec ResolveForConnection(AltruistConnection connection) => Resolve(connection.TransportMode);

    /// <inheritdoc/>
    public ICodec? GetByName(string providerName)
        => _codecs.TryGetValue(providerName, out var codec) ? codec : null;
}

/// <summary>
/// Finds and builds <see cref="CodecProviderAttribute"/> codecs by name, and registers the application's
/// <see cref="ICodec"/>: the codec named by <c>altruist:server:transport:codec:provider</c> (default
/// <see cref="DefaultProvider"/>). Inject <see cref="ICodec"/> for the global codec; inject <see cref="ICodecResolver"/>
/// when the codec may differ per transport.
/// </summary>
public sealed class CodecProviders
{
    /// <summary>Config key of the global codec provider.</summary>
    public const string GlobalProviderKey = "altruist:server:transport:codec:provider";

    /// <summary>The provider used when <see cref="GlobalProviderKey"/> is not set.</summary>
    public const string DefaultProvider = "messagepack";

    private CodecProviders() { }

    /// <summary>The application's global <see cref="ICodec"/> (a DI bean; singleton).</summary>
    /// <param name="services">Provider the codec's own dependencies are resolved from.</param>
    /// <param name="provider">The configured provider name.</param>
    /// <exception cref="InvalidOperationException">No codec has that provider name.</exception>
    [Bean]
    public static ICodec GlobalCodec(IServiceProvider services, [AppConfigValue(GlobalProviderKey, DefaultProvider)] string provider)
    {
        var type = FindTypes().FirstOrDefault(t => string.Equals(t.Name, provider, StringComparison.OrdinalIgnoreCase)).Type
            ?? throw new InvalidOperationException($"{GlobalProviderKey} is '{provider}', but no codec has [CodecProvider(\"{provider}\")].");
        return (ICodec)ActivatorUtilities.CreateInstance(services, type);
    }

    /// <summary>Every concrete <see cref="ICodec"/> with a <see cref="CodecProviderAttribute"/> in the loaded assemblies.</summary>
    public static IEnumerable<(string Name, Type Type)> FindTypes() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .SelectMany(TypeDiscovery.SafeGetTypes)
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(ICodec).IsAssignableFrom(t))
            .Select(t => (Attribute: t.GetCustomAttribute<CodecProviderAttribute>(), Type: t))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Attribute!.Name, x.Type));
}
