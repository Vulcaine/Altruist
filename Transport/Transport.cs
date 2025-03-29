using Microsoft.AspNetCore.Builder;

namespace Altruist.Transport;

public interface ITransport
{
    void UseTransportEndpoints<TType>(IApplicationBuilder app, string path);
    void UseTransportEndpoints(IApplicationBuilder app, Type type, string path);
}
