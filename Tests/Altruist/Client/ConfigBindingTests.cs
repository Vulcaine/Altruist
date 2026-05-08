using Altruist.Client;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// Verifies <see cref="ClientTransportConfig"/> binds correctly from the
/// expected <c>altruist:client:transport</c> YAML / IConfiguration shape.
/// Failure here means downstream <see cref="AltruistClientRouter"/> would pick
/// the wrong host/port/codec at runtime.
/// </summary>
public sealed class ConfigBindingTests
{
    [Fact]
    public void Binds_AllThreeTransports_FromInMemoryConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["altruist:client:transport:defaultTransport"] = "udp",
                ["altruist:client:transport:tcp:host"] = "10.0.0.1",
                ["altruist:client:transport:tcp:port"] = "5566",
                ["altruist:client:transport:tcp:codec:provider"] = "messagepack",
                ["altruist:client:transport:udp:host"] = "10.0.0.2",
                ["altruist:client:transport:udp:port"] = "5567",
                ["altruist:client:transport:udp:codec:provider"] = "messagepack",
                ["altruist:client:transport:ws:host"] = "10.0.0.3",
                ["altruist:client:transport:ws:port"] = "5568",
                ["altruist:client:transport:ws:codec:provider"] = "json",
            })
            .Build();

        var bound = new ClientTransportConfig();
        config.GetSection("altruist:client:transport").Bind(bound);

        Assert.Equal("udp", bound.DefaultTransport);

        Assert.NotNull(bound.Tcp);
        Assert.Equal("10.0.0.1", bound.Tcp!.Host);
        Assert.Equal(5566, bound.Tcp.Port);
        Assert.Equal("messagepack", bound.Tcp.Codec.Provider);

        Assert.NotNull(bound.Udp);
        Assert.Equal("10.0.0.2", bound.Udp!.Host);
        Assert.Equal(5567, bound.Udp.Port);

        Assert.NotNull(bound.Ws);
        Assert.Equal("10.0.0.3", bound.Ws!.Host);
        Assert.Equal(5568, bound.Ws.Port);
        Assert.Equal("json", bound.Ws.Codec.Provider);
    }

    [Fact]
    public void Defaults_WhenSectionMissing_KeepConstructorInitializers()
    {
        var bound = new ClientTransportConfig();
        Assert.Equal("tcp", bound.DefaultTransport);
        Assert.Null(bound.Tcp);
        Assert.Null(bound.Udp);
        Assert.Null(bound.Ws);
    }

    [Fact]
    public void OnlyOneTransport_ConfiguredOthersStayNull()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["altruist:client:transport:tcp:host"] = "127.0.0.1",
                ["altruist:client:transport:tcp:port"] = "5566",
                ["altruist:client:transport:tcp:codec:provider"] = "messagepack",
            })
            .Build();

        var bound = new ClientTransportConfig();
        config.GetSection("altruist:client:transport").Bind(bound);

        Assert.NotNull(bound.Tcp);
        Assert.Null(bound.Udp);
        Assert.Null(bound.Ws);
    }
}
