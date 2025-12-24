using System.Linq.Expressions;
using System.Reflection;

using Altruist.Web.Features;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

[Service]
internal sealed class PortalWarmup
{
    private readonly IServiceProvider _sp;
    private readonly IAltruistContext _ctx;
    private readonly ILogger<PortalWarmup> _log;

    public PortalWarmup(IServiceProvider sp, IAltruistContext ctx, ILogger<PortalWarmup> log)
    {
        _sp = sp;
        _ctx = ctx;
        _log = log;
    }

    [PostConstruct]
    public Task WarmAsync()
    {
        var portals = PortalDiscovery.Discover().Distinct().ToArray();

        foreach (var d in portals)
        {
            _ctx.AddEndpoint(d.Path);

            var instance = _sp.GetRequiredService(d.PortalType);

            if (instance is IPortal portal)
                PortalGateRegistry<IPortal>.RegisterInstance(portal);

            RegisterGateMethodsFromInstance(instance, _log);

            _log.LogDebug("🔥 Warmed up portal {PortalType}.", d.PortalType.FullName);
        }

        return Task.CompletedTask;
    }

    private static void RegisterGateMethodsFromInstance(object instance, ILogger log)
    {
        var type = instance.GetType();

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<GateAttribute>() != null);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<GateAttribute>()!;
            var pars = method.GetParameters();

            var rt = method.ReturnType;
            var isTask = rt == typeof(Task) ||
                         (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>));

            if (!isTask)
                throw new InvalidOperationException(
                    $"Method {type.Name}.{method.Name} marked with [Gate] must return Task or Task<T>.");

            if (pars.Length > 2)
                throw new InvalidOperationException(
                    $"Method {type.Name}.{method.Name} marked with [Gate] must have 0, 1 or 2 parameters.");

            if (pars.Length == 1 && pars[0].ParameterType != typeof(string))
                throw new InvalidOperationException(
                    $"Method {type.Name}.{method.Name} with a single parameter must have signature (string clientId).");

            if (pars.Length == 2)
            {
                if (!typeof(IPacket).IsAssignableFrom(pars[0].ParameterType))
                    throw new InvalidOperationException(
                        $"Method {type.Name}.{method.Name} first parameter must implement IPacket.");
                if (pars[1].ParameterType != typeof(string))
                    throw new InvalidOperationException(
                        $"Method {type.Name}.{method.Name} second parameter must be string (clientId).");
            }

            var delegateType = Expression.GetDelegateType(
                pars.Select(p => p.ParameterType)
                    .Concat(new[] { typeof(Task) })
                    .ToArray());

            var del = method.CreateDelegate(delegateType, instance);
            PortalGateRegistry<IPortal>.Register(attr.Event, del);

            log.LogDebug("🔒 Registered gate for event '{Event}' -> {Method} on {Type}.",
                attr.Event, method.Name, type.FullName);
        }
    }
}
