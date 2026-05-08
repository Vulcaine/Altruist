namespace Altruist.Client;

/// <summary>
/// Picks an <see cref="IClientCodec"/> by its <see cref="IClientCodec.Provider"/>
/// name. DI populates the candidate set with every <see cref="IClientCodec"/>
/// registered via <c>[Service(typeof(IClientCodec))]</c>; the router asks for
/// "messagepack" or "json" based on the <c>config.yml</c> codec block.
///
/// <para>Mirrors the server's <c>CodecResolver</c> shape so a developer who
/// configures the server already knows how to configure the client.</para>
/// </summary>
[Service]
public sealed class ClientCodecResolver
{
    private readonly Dictionary<string, IClientCodec> _byProvider;

    public ClientCodecResolver(IEnumerable<IClientCodec> codecs)
    {
        if (codecs is null) throw new ArgumentNullException(nameof(codecs));
        _byProvider = new Dictionary<string, IClientCodec>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in codecs)
        {
            if (c is null) continue;
            // Last-wins on duplicates — matches the server's "last [Service] loaded wins" behaviour
            // so a consumer can override the default codec by registering their own [Service].
            _byProvider[c.Provider] = c;
        }
    }

    /// <summary>
    /// Resolve the codec by provider name. <c>"msgpack"</c> is treated as an
    /// alias for <c>"messagepack"</c>.
    /// </summary>
    public IClientCodec Resolve(string providerName)
    {
        var name = (providerName ?? "messagepack").Trim();
        if (name.Equals("msgpack", StringComparison.OrdinalIgnoreCase))
            name = "messagepack";

        if (_byProvider.TryGetValue(name, out var codec))
            return codec;

        var available = string.Join(", ", _byProvider.Keys);
        throw new InvalidOperationException(
            $"Unknown codec provider '{providerName}'. Available: [{available}].");
    }
}
