/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Altruist.ThreeD.Numerics;

#nullable enable

namespace Altruist
{
    // ───────────────────────────────────────────────────────────────────────────
    // Prefab (3D)
    // ───────────────────────────────────────────────────────────────────────────
    public interface IPrefab3D : IPrefab, IVaultModel
    {
        Transform3D Transform { get; set; }
        PrefabRootRef Root { get; set; }
        Dictionary<string, List<ModelRef>> Edges { get; set; }
    }

    public class Prefab3D : VaultModel, IPrefab3D
    {
        public override string Type { get; set; } = "prefab.manifest.3d";
        public override DateTime Timestamp { get; set; }

        public override string StorageId { get; set; } = "";
        public virtual string InstanceId { get; set; } = "";
        public virtual string RoomId { get; set; } = "";

        public virtual string PrefabId { get; set; } = "";
        public virtual string? DisplayName { get; set; }

        public virtual Transform3D Transform { get; set; } = Transform3D.Zero;

        public PrefabRootRef Root { get; set; } = PrefabRootRef.Empty;

        /// Parent.StorageId -> children (StorageId, Type, Path). Structure only.
        public Dictionary<string, List<ModelRef>> Edges { get; set; } = new(StringComparer.Ordinal);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Builder (3D; accepts only IVaultModel; no DB writes)
    // ───────────────────────────────────────────────────────────────────────────
    [Service(typeof(IPrefabBuilder))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    public class PrefabBuilder3D : IPrefabBuilder, INodeBuilder
    {
        private readonly Prefab3D _prefab;
        private readonly Stack<(IVaultModel model, string path)> _stack = new();

        public PrefabBuilder3D(Prefab3D prefab)
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
            public RootSentinel(Prefab3D owner) { Type = "prefab.root"; GroupId = owner.StorageId; }
            public string StorageId { get; set; } = "";
            public string GroupId { get; set; } = "";
            public string Type { get; set; } = "prefab.root";
            public DateTime Timestamp { get; set; }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Prefab Manager (3D)
    // ───────────────────────────────────────────────────────────────────────────
    public interface IPrefabManager3D
    {
        Task SaveAsync(Prefab3D prefab, IKeyspace keyspace);
        Task<IPrefabHandle<Prefab3D>> LoadAsync(string prefabSysId, IKeyspace keyspace);
    }

    [Service(typeof(IPrefabManager3D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    public sealed class PrefabManager3D : PrefabManagerBase, IPrefabManager3D
    {
        public PrefabManager3D(ICacheProvider cache, IVaultRepository<IKeyspace> repo, IModelTypeRegistry types)
            : base(cache, repo, types) { }

        public Task<IPrefabHandle<Prefab3D>> LoadAsync(string prefabSysId, IKeyspace keyspace)
            => LoadCoreAsync<Prefab3D>(prefabSysId, keyspace, p => p.Root, p => p.Edges);

        public Task SaveAsync(Prefab3D prefab, IKeyspace keyspace)
            => SaveCoreAsync(prefab, keyspace);
    }
}
