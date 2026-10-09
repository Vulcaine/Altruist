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
/// Resolution order: transport-specific config → global config → first available.
///
/// Discovers all [CodecProvider("name")] types at startup and instantiates them on demand.
/// This works independently of ConditionalOnConfig — codecs that aren't the global default
/// can still be used for specific transports.
/// </summary>
/// <remarks>
/// Config keys: <c>altruist:server:transport:&lt;mode&gt;:codec:provider</c> (per transport, e.g. <c>websocket</c>,
/// <c>tcp</c>) and <c>altruist:server:transport:codec:provider</c> (global). Unknown names fall through to the next step
/// silently. Registered as a singleton by <see cref="CodecResolver"/> only when <c>altruist:server:transport</c> is
/// configured. Inject this instead of <see cref="ICodec"/> when the codec must follow config or differ per transport.
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
    /// <returns>The per-transport codec, else the global default, else the first discovered codec.</returns>
    /// <exception cref="InvalidOperationException">No <see cref="CodecProviderAttribute"/> codec exists at all.</exception>
    ICodec Resolve(string? transportMode = null);

    /// <summary>
    /// Resolve the codec based on the connection type (WebSocketConnection → "websocket", etc.).
    /// </summary>
    /// <remarks>The mode is inferred from the connection's class name: containing "WebSocket" maps to <c>websocket</c>,
    /// containing "Tcp" to <c>tcp</c>, anything else to the global default.</remarks>
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

    /// <summary>Discovers and instantiates all codec providers. Constructed by DI.</summary>
    public CodecResolver(IServiceProvider serviceProvider, IConfiguration config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<CodecResolver>();

        // 1. Collect DI-registered codecs (the global default from ConditionalOnConfig)
        var diCodecs = serviceProvider.GetServices<ICodec>();
        foreach (var codec in diCodecs)
        {
            var attr = codec.GetType().GetCustomAttribute<CodecProviderAttribute>();
            if (attr != null)
                _codecs[attr.Name] = codec;
        }

        // 2. Discover all [CodecProvider] types not yet registered (for per-transport overrides)
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName));

        foreach (var assembly in assemblies)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface || !typeof(ICodec).IsAssignableFrom(type))
                        continue;

                    var providerAttr = type.GetCustomAttribute<CodecProviderAttribute>();
                    if (providerAttr == null || _codecs.ContainsKey(providerAttr.Name))
                        continue;

                    // Instantiate codec not yet in DI (needed for per-transport override)
                    try
                    {
                        var instance = ActivatorUtilities.CreateInstance(serviceProvider, type) as ICodec;
                        if (instance != null)
                        {
                            _codecs[providerAttr.Name] = instance;
                            _logger.LogDebug("Discovered codec provider: {Name} ({Type})", providerAttr.Name, type.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to instantiate codec provider {Name} ({Type})",
                            providerAttr.Name, type.Name);
                    }
                }
            }
            catch (ReflectionTypeLoadException) { }
        }

        if (_codecs.Count > 0)
            _logger.LogInformation("Available codec providers: {Codecs}", string.Join(", ", _codecs.Keys));
    }

    /// <inheritdoc/>
    public ICodec Resolve(string? transportMode = null)
    {
        // 1. Transport-specific override
        if (!string.IsNullOrEmpty(transportMode))
        {
            var specific = _config[$"altruist:server:transport:{transportMode}:codec:provider"];
            if (!string.IsNullOrEmpty(specific) && _codecs.TryGetValue(specific, out var c))
                return c;
        }

        // 2. Global default
        var global = _config["altruist:server:transport:codec:provider"];
        if (!string.IsNullOrEmpty(global) && _codecs.TryGetValue(global, out var g))
            return g;

        // 3. First available
        if (_codecs.Count > 0)
            return _codecs.Values.First();

        throw new InvalidOperationException(
            "No codec providers found. Implement ICodec with [CodecProvider(\"name\")] " +
            "and configure altruist:server:transport:codec:provider in config.yml.");
    }

    /// <inheritdoc/>
    public ICodec ResolveForConnection(AltruistConnection connection)
    {
        var transportMode = connection switch
        {
            _ when connection.GetType().Name.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) => "websocket",
            _ when connection.GetType().Name.Contains("Tcp", StringComparison.OrdinalIgnoreCase) => "tcp",
            _ => null
        };

        return Resolve(transportMode);
    }

    /// <inheritdoc/>
    public ICodec? GetByName(string providerName)
        => _codecs.TryGetValue(providerName, out var codec) ? codec : null;
}
