/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming
{
    /// <summary>
    /// General-purpose factory: constructs any class via DI and runs [PostConstruct] (void/Task/ValueTask).
    /// Useful for non-prefab helpers or shared utils. Prefab-specific factories live in 2D/3D namespaces.
    /// </summary>
    public interface IPrefabFactory
    {
        Task<T> CreateAsync<T>() where T : class;
        Task<object> CreateAsync(Type type);
    }

    [Service(typeof(IPrefabFactory))]
    public sealed class PrefabFactory : IPrefabFactory
    {
        private readonly IServiceProvider _sp;
        private readonly IConfiguration _cfg;
        private readonly ILoggerFactory _lf;

        public PrefabFactory(IServiceProvider sp, IConfiguration cfg, ILoggerFactory lf)
        {
            _sp = sp;
            _cfg = cfg;
            _lf = lf;
        }

        public async Task<T> CreateAsync<T>() where T : class
        {
            var logger = _lf.CreateLogger<PrefabFactory>();
            var instance = ActivatorUtilities.CreateInstance<T>(_sp)!;
            await DependencyResolver.InvokePostConstructAsync(instance, _sp, _cfg, logger);
            return instance;
        }

        public async Task<object> CreateAsync(Type type)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));
            var logger = _lf.CreateLogger<PrefabFactory>();
            var instance = ActivatorUtilities.CreateInstance(_sp, type)!;
            await DependencyResolver.InvokePostConstructAsync(instance, _sp, _cfg, logger);
            return instance;
        }
    }
}
