/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.TwoD.Numerics;

namespace Altruist
{
    // ───────────────────────────────────────────────────────────────────────────
    // Prefab (2D)
    // ───────────────────────────────────────────────────────────────────────────
    public interface IPrefab
    {
        string PrefabId { get; }
        string InstanceId { get; set; }
        string RoomId { get; set; }
    }

    public interface IPrefab2D : IPrefab, IVaultModel
    {
        Transform2D Transform { get; set; }
        PrefabRootRef Root { get; set; }
        Dictionary<string, List<ModelRef>> Edges { get; set; }
    }

    public class Prefab2D : VaultModel, IPrefab2D
    {
        public override string Type { get; set; } = "prefab.manifest.2d";
        public override DateTime Timestamp { get; set; }

        public override string StorageId { get; set; } = "";
        public virtual string InstanceId { get; set; } = "";
        public virtual string RoomId { get; set; } = "";

        public virtual string PrefabId { get; set; } = "";
        public virtual string? DisplayName { get; set; }

        public virtual Transform2D Transform { get; set; } = Transform2D.Zero;

        public PrefabRootRef Root { get; set; } = PrefabRootRef.Empty;

        /// Parent.StorageId -> children (StorageId, Type, Path). Structure only.
        public Dictionary<string, List<ModelRef>> Edges { get; set; } = new(StringComparer.Ordinal);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Builder (2D; accepts only IVaultModel; no DB writes)
    // ───────────────────────────────────────────────────────────────────────────
    [Service(typeof(IPrefabBuilder))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    public class PrefabBuilder2D : IPrefabBuilder, INodeBuilder
    {
        private readonly Prefab2D _prefab;
        private readonly Stack<(IVaultModel model, string path)> _stack = new();

        public PrefabBuilder2D(Prefab2D prefab)
        {
            _prefab = prefab;
            _prefab.Root = PrefabRootRef.Empty;
            _stack.Push((new RootSentinel(prefab), "Root"));
        }

        public INodeBuilder Root => this;
        public string Path => _stack.Peek().path;
        public IVaultModel Model => _stack.Peek().model;

        public INodeBuilder AddChild<TModel>(TModel model, string? name = null) where TModel : class, IVaultModel
        {
            var (parentModel, parentPath) = _stack.Peek();
            var nodeName = Tail(model.Type);
            if (!string.IsNullOrWhiteSpace(name)) nodeName = name!;
            var path = parentPath == "Root" ? $"Root/{nodeName}" : $"{parentPath}/{nodeName}";

            if (string.IsNullOrEmpty(_prefab.Root.StorageId))
                _prefab.Root = new PrefabRootRef(model.StorageId, model.Type, "Root");

            var isRootSentinel = ReferenceEquals(parentModel, _stack.First().model);
            var parentId = isRootSentinel ? _prefab.Root.StorageId : parentModel.StorageId;

            if (!_prefab.Edges.TryGetValue(parentId, out var list))
                _prefab.Edges[parentId] = list = new List<ModelRef>();

            list.Add(ModelRef.Of(model, path));
            _stack.Push((model, path));
            return this;
        }

        private static string Tail(string t) => t.Contains('.') ? t[(t.LastIndexOf('.') + 1)..] : t;

        private sealed class RootSentinel : IVaultModel
        {
            public RootSentinel(Prefab2D owner) { Type = "prefab.root"; GroupId = owner.StorageId; }
            public string StorageId { get; set; } = "";
            public string GroupId { get; set; } = "";
            public string Type { get; set; } = "prefab.root";
            public DateTime Timestamp { get; set; }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Prefab Manager (2D)
    // ───────────────────────────────────────────────────────────────────────────
    public interface IPrefabManager2D
    {
        Task SaveAsync(Prefab2D prefab, IKeyspace keyspace);
        Task<IPrefabHandle<Prefab2D>> LoadAsync(string prefabSysId, IKeyspace keyspace);
    }

    [Service(typeof(IPrefabManager2D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    public sealed class PrefabManager2D : PrefabManagerBase, IPrefabManager2D
    {
        public PrefabManager2D(ICacheProvider cache, IVaultRepository<IKeyspace> repo, IModelTypeRegistry types)
            : base(cache, repo, types) { }

        public Task<IPrefabHandle<Prefab2D>> LoadAsync(string prefabSysId, IKeyspace keyspace)
            => LoadCoreAsync<Prefab2D>(prefabSysId, keyspace, p => p.Root, p => p.Edges);

        public Task SaveAsync(Prefab2D prefab, IKeyspace keyspace)
            => SaveCoreAsync(prefab, keyspace);
    }
}
