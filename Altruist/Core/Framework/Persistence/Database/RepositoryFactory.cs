/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist
{
    [Service(typeof(IRepositoryFactory))]
    public sealed class RepositoryFactory : IRepositoryFactory
    {
        private readonly IServiceProvider _sp;
        private readonly IKeyspace[] _keyspaces;

        public RepositoryFactory(IServiceProvider sp, IKeyspace[] keyspaces)
        {
            _sp = sp ?? throw new ArgumentNullException(nameof(sp));
            // materialize all registered keyspaces so we can find by name quickly
            _keyspaces = keyspaces;
        }

        public IVaultRepository<TKeyspace> Make<TKeyspace>() where TKeyspace : class, IKeyspace
        {
            var repo = _sp.GetService<IVaultRepository<TKeyspace>>();
            if (repo is null)
                throw new InvalidOperationException($"No IVaultRepository<{typeof(TKeyspace).Name}> is registered.");

            return repo;
        }

        public IAnyVaultRepository Make(string keyspaceName)
        {
            if (string.IsNullOrWhiteSpace(keyspaceName))
                throw new ArgumentException("Keyspace name is required.", nameof(keyspaceName));

            var ks = _keyspaces.FirstOrDefault(k =>
                string.Equals(k.Name, keyspaceName, StringComparison.OrdinalIgnoreCase));

            if (ks is null)
                throw new InvalidOperationException($"Keyspace '{keyspaceName}' is not registered.");

            // Resolve the typed repo for the *concrete* keyspace type and wrap it
            var repoType = typeof(IVaultRepository<>).MakeGenericType(ks.GetType());
            var repoObj = _sp.GetService(repoType)
                         ?? throw new InvalidOperationException($"No repository registered for keyspace type '{ks.GetType().Name}'.");

            return new AnyVaultRepositoryAdapter(repoObj, ks);
        }

        // ------------------ adapter ------------------

        private sealed class AnyVaultRepositoryAdapter : IAnyVaultRepository
        {
            private readonly object _typedRepo;
            public IKeyspace Keyspace { get; }

            public AnyVaultRepositoryAdapter(object typedRepo, IKeyspace keyspace)
            {
                _typedRepo = typedRepo ?? throw new ArgumentNullException(nameof(typedRepo));
                Keyspace = keyspace ?? throw new ArgumentNullException(nameof(keyspace));
            }

            public IDatabaseServiceToken Token
            {
                get
                {
                    dynamic d = _typedRepo;
                    return (IDatabaseServiceToken)d.Token;
                }
            }

            public IVault<TVaultModel> Select<TVaultModel>() where TVaultModel : class, IVaultModel
            {
                dynamic d = _typedRepo;
                return (IVault<TVaultModel>)d.Select<TVaultModel>();
            }
        }
    }
}
