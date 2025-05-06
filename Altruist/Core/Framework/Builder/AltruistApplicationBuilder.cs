using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

public class AltruistApplicationBuilder
{
    protected readonly IServiceCollection Services;
    protected readonly IAltruistContext Settings;
    private string[] _args;

    public AltruistApplicationBuilder(IServiceCollection services, IAltruistContext settings, string[] args)
    {
        Services = services;
        Settings = settings;
        _args = args;
    }

    public AltruistApplicationBuilder Codec<TCodec>() where TCodec : class, ICodec
    {
        Services.AddSingleton<TCodec>();
        return this;
    }

    public AltruistWebServerBuilder WebApp(Func<WebApplicationBuilder, WebApplicationBuilder>? setup = null)
    {
        var app = WebApiHelper.Create(_args, Services);
        if (setup != null)
        {
            return new AltruistWebServerBuilder(setup(app), Settings);
        }
        return new AltruistWebServerBuilder(app, Settings);
    }
}

public class AltruistWebApplicationBuilder
{
    protected readonly WebApplicationBuilder Builder;
    protected readonly IAltruistContext Settings;

    public AltruistWebApplicationBuilder(WebApplicationBuilder builder, IAltruistContext settings)
    {
        Builder = builder;
        Settings = settings;
    }

    public AppManager Configure(Func<WebApplication, WebApplication> setup)
    {
        ServiceConfig.Configure(Builder.Services);
        return new AppManager(setup!(Builder.Build()));
    }

    public void StartServer()
    {
        new AltruistWebServerBuilder(Builder, Settings).StartServer();
    }
}
