/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Altruist.Database;
using Altruist.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Contracts;

public interface IConfiguration
{
    void Configure(IServiceCollection services);
}

public interface ICacheConfiguration : IConfiguration
{

}

public interface ITransportConfiguration : IConfiguration
{

}

public interface IDatabaseConfiguration : IConfiguration
{
    string DatabaseName { get; }
}

public interface IServiceToken<TConfiguration> where TConfiguration : IConfiguration
{
    public string Description { get; }
    public TConfiguration Configuration { get; }
}

public interface ICacheServiceToken : IServiceToken<ICacheConfiguration>
{

}

public interface ITransportServiceToken : IServiceToken<ITransportConfiguration>
{

}

public interface IDatabaseServiceToken : IServiceToken<IDatabaseConfiguration>
{

}

public interface ISetup<TSelf> where TSelf : ISetup<TSelf>
{
    Task Build(IAltruistContext settings);
}

public interface IContactSetup<TSelf> : ISetup<TSelf> where TSelf : IContactSetup<TSelf>
{
    TSelf AddContactPoint(string host, int port);
    TSelf AddContactPoint(string connectionString);
}

public interface IReconnectSetup<TSelf> : ISetup<TSelf> where TSelf : IReconnectSetup<TSelf>
{
    TSelf Reconnect(int afterSeconds);
}

public interface IExternalContactSetup<TSelf> : IContactSetup<TSelf>, IReconnectSetup<TSelf> where TSelf : IExternalContactSetup<TSelf>
{

}

public abstract class ExternalConnectionSetupBase<TSelf> : ConnectionSetupBase<TSelf>, IReconnectSetup<TSelf> where TSelf : ExternalConnectionSetupBase<TSelf>
{
    protected int _reconnectAfterSeconds = 0;
    protected ExternalConnectionSetupBase(IServiceCollection services) : base(services)
    {

    }

    public TSelf Reconnect(int afterSeconds)
    {
        _reconnectAfterSeconds = afterSeconds;
        return (TSelf)this;
    }
}

// Base class for general connection setups
public abstract class ConnectionSetupBase<TSelf> : IContactSetup<TSelf>
    where TSelf : ConnectionSetupBase<TSelf>
{
    protected readonly IServiceCollection _services;
    protected List<string> _contactPoints = new List<string>();

    protected ConnectionSetupBase(IServiceCollection services)
    {
        _services = services;
    }

    public virtual TSelf AddContactPoint(string host, int port)
    {
        _contactPoints.Add($"{host}:{port}");
        return (TSelf)this;
    }

    public virtual TSelf AddContactPoint(string connectionString)
    {
        _contactPoints.Add(connectionString);
        return (TSelf)this;
    }

    public abstract Task Build(IAltruistContext settings);
}

public interface ICacheConnectionSetup<TSelf> : IExternalContactSetup<TSelf> where TSelf : ICacheConnectionSetup<TSelf> { }

public interface ICacheConnectionSetupBase { }

public abstract class CacheConnectionSetup<TSelf> : ExternalConnectionSetupBase<TSelf>, ICacheConnectionSetup<TSelf> where TSelf : CacheConnectionSetup<TSelf>
{
    protected CacheConnectionSetup(IServiceCollection services) : base(services)
    {
    }
}

public interface IDatabaseConnectionSetup<TSelf> : IExternalContactSetup<TSelf> where TSelf : IDatabaseConnectionSetup<TSelf> { }

public abstract class DatabaseConnectionSetup<TSelf> : ExternalConnectionSetupBase<TSelf>, IDatabaseConnectionSetup<TSelf> where TSelf : DatabaseConnectionSetup<TSelf>
{
    protected Dictionary<string, IKeyspaceSetup> Keyspaces { get; } = new();
    protected IDatabaseServiceToken Token { get; }

    protected DatabaseConnectionSetup(IServiceCollection services, IDatabaseServiceToken token) : base(services)
    {
        Token = token;
    }

    public abstract TSelf CreateKeyspace<TKeyspace>(Func<KeyspaceSetup<TKeyspace>, KeyspaceSetup<TKeyspace>>? setupAction = null) where TKeyspace : class, IKeyspace, new();
}

public interface ITransportConnectionSetup<TSelf> : ISetup<TSelf>
    where TSelf : ITransportConnectionSetup<TSelf>
{
    TSelf MapPortal<P>(string path) where P : Portal, IPortal;

    TSelf MapPortal<P, TImplementation>(string path, Func<IServiceProvider, TImplementation> implementationFactory) where P : Portal, IPortal where TImplementation : class, P;

    TSelf MapRelayPortal<P>(string host, int port, string eventName) where P : RelayPortal;

    TSelf MapRelayPortal<P, TImplementation>(string host, int port, string eventName, Func<IServiceProvider, TImplementation> implementationFactory) where P : RelayPortal where TImplementation : class, P;

    TSelf SetCodec(ICodec encoder);
}

public interface ITransportConnectionSetupBase
{
    Dictionary<Type, (string Path, Func<IServiceProvider, IPortal>? Factory)> Portals { get; }
}

public abstract class TransportConnectionSetupBase<TSelf> : ITransportConnectionSetup<TSelf>, ITransportConnectionSetupBase where TSelf : TransportConnectionSetupBase<TSelf>
{
    public Dictionary<Type, (string Path, Func<IServiceProvider, IPortal>? Factory)> Portals { get; } = new();
    protected readonly IServiceCollection _services;
    protected readonly IAltruistContext _altruistContext;
    public TransportConnectionSetupBase(IServiceCollection services)
    {
        _services = services;
        _altruistContext = services.BuildServiceProvider().GetRequiredService<IAltruistContext>();
    }
    public abstract Task Build(IAltruistContext settings);
    public abstract TSelf MapPortal<P>(string path) where P : Portal, IPortal;
    public abstract TSelf MapPortal<P, TImplementation>(string path, Func<IServiceProvider, TImplementation> implementationFactory) where P : Portal, IPortal where TImplementation : class, P;
    public abstract TSelf MapRelayPortal<P>(string host, int port, string eventName) where P : RelayPortal;
    public abstract TSelf MapRelayPortal<P, TImplementation>(string host, int port, string eventName, Func<IServiceProvider, TImplementation> implementationFactory) where P : RelayPortal where TImplementation : class, P;

    public TSelf SetCodec(ICodec codec)
    {
        _services.AddSingleton(codec.Encoder);
        _services.AddSingleton(codec.Decoder);
        _services.AddSingleton(codec);
        return (TSelf)this;
    }


}

public abstract class TransportConnectionSetup<TSelf> : TransportConnectionSetupBase<TSelf>
    where TSelf : TransportConnectionSetup<TSelf>
{
    private readonly IAltruistContext _settings;

    protected TransportConnectionSetup(IServiceCollection services, IAltruistContext settings)
        : base(services)
    {
        _settings = settings;
    }


    public override TSelf MapPortal<P>(string path)
    {
        Portals[typeof(P)] = (path, null);
        return (TSelf)this;
    }

    public override TSelf MapPortal<P, TImplementation>(string path, Func<IServiceProvider, TImplementation> implementationFactory)
    {
        Portals[typeof(P)] = (path, implementationFactory);
        return (TSelf)this;
    }

    public override TSelf MapRelayPortal<P, TImplementation>(string host, int port, string eventName, Func<IServiceProvider, TImplementation> implementationFactory)
    {
        RegisterRelayPortalServices<P, TImplementation>(host, port, eventName, implementationFactory);
        return (TSelf)this;
    }

    public override TSelf MapRelayPortal<P>(string host, int port, string eventName)
    {
        RegisterRelayPortalServices<P, P>(host, port, eventName, sp => ActivatorUtilities.CreateInstance<P>(sp));
        return (TSelf)this;
    }

    private void RegisterRelayPortalServices<P, TImplementation>(string host, int port, string eventName, Func<IServiceProvider, TImplementation> implementationFactory)
        where P : RelayPortal
        where TImplementation : P
    {
        _services.AddSingleton<IRelayService>(sp =>
        {
            var instance = implementationFactory(sp);
            return new AltruistRelayService(
                "ws",
                host,
                port,
                eventName,
                instance,
                sp.GetService<ICodec>()!,
                sp.GetService<ILoggerFactory>()!,
                sp.GetService<ITransportClient>()!
            );
        });

        _services.AddSingleton(sp =>
        {
            var instance = ActivatorUtilities.CreateInstance<P>(sp);
            IRelayService relayService = sp.GetRequiredService<IRelayService>();

            IInterceptor interceptor = new RelayInterceptor(relayService);
            instance.AddInterceptor(interceptor);
            return instance;
        });
    }

    public override Task Build(IAltruistContext settings)
    {
        foreach (var portal in Portals)
        {
            var type = portal.Key;
            var path = portal.Value.Path;
            var factory = portal.Value.Factory;

            if (factory != null)
            {
                _services.AddSingleton(type, factory);
            }
            else
            {
                _services.AddSingleton(type);
            }

            _settings.AddEndpoint(path);

            if (typeof(IPortal).IsAssignableFrom(type))
            {
                _services.AddSingleton(typeof(IPortal), sp => sp.GetRequiredService(type));
            }
            else
            {
                throw new InvalidOperationException($"Portal type {type.Name} does not implement IPortal.");
            }
        }

        return Task.CompletedTask;
    }
}

