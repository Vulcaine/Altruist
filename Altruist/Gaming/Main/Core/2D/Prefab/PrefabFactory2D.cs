using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.TwoD;

public interface IPrefabFactory2D
{
    Task<TPrefab> CreateAsync<TPrefab>() where TPrefab : Prefab2D;
    Task<TPrefab> CreateAsync<TPrefab>(Action<PrefabConfigContext2D> configure) where TPrefab : Prefab2D;
    Task<Prefab2D> CreateAsync(Type concretePrefabType, Action<PrefabConfigContext2D>? configure = null);
}

[Service(typeof(IPrefabFactory2D))]
public sealed class PrefabFactory2D : IPrefabFactory2D
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _cfg;
    private readonly ILoggerFactory _lf;
    private readonly IPrefabEditor2D _editor;

    public PrefabFactory2D(
        IPrefabEditor2D editor,
        IServiceProvider sp,
        IConfiguration cfg,
        ILoggerFactory lf)
    {
        _sp = sp;
        _cfg = cfg;
        _lf = lf;
        _editor = editor;
    }

    public async Task<TPrefab> CreateAsync<TPrefab>() where TPrefab : Prefab2D =>
        await CreateAsync<TPrefab>(null);

    public async Task<TPrefab> CreateAsync<TPrefab>(Action<PrefabConfigContext2D>? configure)
        where TPrefab : Prefab2D
    {
        var prefab = ActivatorUtilities.CreateInstance<TPrefab>(_sp);
        await DependencyResolver.InvokePostConstructAsync(prefab, _sp, _cfg, _lf.CreateLogger<PrefabFactory2D>());

        if (configure is not null)
            configure(new PrefabConfigContext2D(_editor.Edit(prefab)));

        return prefab;
    }

    public async Task<Prefab2D> CreateAsync(Type prefabType, Action<PrefabConfigContext2D>? configure = null)
    {
        var prefab = (Prefab2D)ActivatorUtilities.CreateInstance(_sp, prefabType)!;
        await DependencyResolver.InvokePostConstructAsync(prefab, _sp, _cfg, _lf.CreateLogger<PrefabFactory2D>());

        configure?.Invoke(new PrefabConfigContext2D(_editor.Edit(prefab)));

        return prefab;
    }
}
