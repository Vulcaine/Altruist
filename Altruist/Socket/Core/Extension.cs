namespace Altruist.Socket;

public static class SocketExtensions
{
    public static IAfterConnectionBuilder WithTCP(this AltruistConnectionBuilder builder, Func<TcpConnectionSetup, TcpConnectionSetup> setup)
    {
        return builder.SetupTransport(TcpTransportToken.Instance, setup);
    }

    public static IAfterConnectionBuilder WithUDP(this AltruistConnectionBuilder builder, Func<UdpConnectionSetup, UdpConnectionSetup> setup)
    {
        return builder.SetupTransport(UdpTransportToken.Instance, setup);
    }
}