/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections;

namespace Altruist.Gaming.Flow;

/// <summary>
/// The entities of a simulation, always iterated in ascending id order (the deterministic order
/// every per-entity loop needs), with O(1) lookup by id and O(log n) position of an id.
/// Replaces "a list sorted after every add + a linear search by id".
///
/// <para>Order: ascending by <typeparamref name="TKey"/>'s <see cref="IComparable{T}"/> (ids are
/// unique, so the order is total and equals a sort of the list after each add). Removal keeps the
/// order of the rest. Iteration (<c>foreach</c>, the indexer) does not allocate; adding or removing
/// while iterating throws, like <see cref="List{T}"/>.</para>
/// </summary>
public class EntityRegistry<TKey, T> : IReadOnlyList<T> where TKey : notnull, IComparable<TKey>
{
    private readonly List<TKey> _keys = new();
    private readonly List<T> _items = new();
    private readonly Dictionary<TKey, T> _byKey = new();

    /// <summary>Number of entities.</summary>
    public int Count => _items.Count;

    /// <summary>The entity at <paramref name="index"/> (id order).</summary>
    public T this[int index] => _items[index];

    /// <summary>The ids in ascending order.</summary>
    public IReadOnlyList<TKey> Ids => _keys;

    /// <summary>Adds an entity; throws when the id exists. Returns its position (id order).</summary>
    public int Add(TKey id, T item)
    {
        if (_byKey.ContainsKey(id)) throw new InvalidOperationException($"Entity {id} already exists.");
        var i = _keys.BinarySearch(id);
        i = ~i;
        _keys.Insert(i, id);
        _items.Insert(i, item);
        _byKey.Add(id, item);
        return i;
    }

    /// <summary>Removes the entity with <paramref name="id"/>; false when there is none.</summary>
    public bool Remove(TKey id)
    {
        if (!_byKey.Remove(id)) return false;
        var i = _keys.BinarySearch(id);
        _keys.RemoveAt(i);
        _items.RemoveAt(i);
        return true;
    }

    /// <summary>True when an entity has <paramref name="id"/>.</summary>
    public bool Contains(TKey id) => _byKey.ContainsKey(id);

    /// <summary>The entity with <paramref name="id"/>.</summary>
    public bool TryGet(TKey id, out T item) => _byKey.TryGetValue(id, out item!);

    /// <summary>The entity with <paramref name="id"/>, or <c>default</c>.</summary>
    public T? Find(TKey id) => _byKey.TryGetValue(id, out var item) ? item : default;

    /// <summary>The position of <paramref name="id"/> in id order, or -1.</summary>
    public int IndexOf(TKey id)
    {
        var i = _keys.BinarySearch(id);
        return i >= 0 ? i : -1;
    }

    /// <summary>Removes every entity.</summary>
    public void Clear()
    {
        _keys.Clear();
        _items.Clear();
        _byKey.Clear();
    }

    /// <summary>Allocation-free enumerator (id order).</summary>
    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

/// <summary>An <see cref="EntityRegistry{TKey, T}"/> keyed by <c>int</c> ids.</summary>
public sealed class EntityRegistry<T> : EntityRegistry<int, T>
{
}
