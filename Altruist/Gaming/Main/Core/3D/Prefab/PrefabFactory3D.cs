using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.ThreeD;

public interface IPrefabFactory3D
{
    Task<TPrefab> CreateAsync<TPrefab>() where TPrefab : Prefab3D;
    Task<TPrefab> CreateAsync<TPrefab>(Action<PrefabConfigContext3D> configure) where TPrefab : Prefab3D;
    Task<Prefab3D> CreateAsync(Type concretePrefabType, Action<PrefabConfigContext3D>? configure = null);
}

[Service(typeof(IPrefabFactory3D))]
[ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
public sealed class PrefabFactory3D : IPrefabFactory3D
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _cfg;
    private readonly ILoggerFactory _lf;
    private readonly IPrefabEditor3D _editor;

    public PrefabFactory3D(
        IPrefabEditor3D editor,
        IServiceProvider sp,
        IConfiguration cfg,
        ILoggerFactory lf)
    {
        _sp = sp;
        _cfg = cfg;
        _lf = lf;
        _editor = editor;
    }

    public async Task<TPrefab> CreateAsync<TPrefab>()
        where TPrefab : Prefab3D =>
        await CreateAsync<TPrefab>(null);

    public async Task<TPrefab> CreateAsync<TPrefab>(Action<PrefabConfigContext3D>? configure)
        where TPrefab : Prefab3D
    {
        var prefab = ActivatorUtilities.CreateInstance<TPrefab>(_sp);
        await DependencyResolver.InvokePostConstructAsync(prefab, _sp, _cfg, _lf.CreateLogger<PrefabFactory3D>());

        if (configure is not null)
            configure(new PrefabConfigContext3D(_editor.Edit(prefab)));

        return prefab;
    }

    public async Task<Prefab3D> CreateAsync(Type prefabType, Action<PrefabConfigContext3D>? configure = null)
    {
        var prefab = (Prefab3D)ActivatorUtilities.CreateInstance(_sp, prefabType)!;
        await DependencyResolver.InvokePostConstructAsync(prefab, _sp, _cfg, _lf.CreateLogger<PrefabFactory3D>());

        configure?.Invoke(new PrefabConfigContext3D(_editor.Edit(prefab)));

        return prefab;
    }
}

