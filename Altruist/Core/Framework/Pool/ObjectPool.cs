/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist;

/// <summary>
/// Centralized, thread-safe object pool for reusing expensive allocations.
/// Eliminates per-system manual caching — any component can rent and return objects.
///
/// Usage:
///   var list = AltruistPool.RentList&lt;int&gt;();
///   list.Add(42);
///   AltruistPool.ReturnList(list);  // auto-cleared
///
///   var dict = AltruistPool.RentDictionary&lt;string, object?&gt;(capacity: 16);
///   AltruistPool.ReturnDictionary(dict);  // auto-cleared
///
/// All collections are cleared on return. Pool is bounded (default 64 per type).
/// </summary>
public static class AltruistPool
{
    private const int DefaultMaxPerType = 64;

    // ── List<T> pool ──

    private static class ListPool<T>
    {
        private static readonly ConcurrentBag<List<T>> _bag = new();

        public static List<T> Rent(int capacity = 0)
        {
            if (_bag.TryTake(out var list)) { list.Clear(); return list; }
            return capacity > 0 ? new List<T>(capacity) : new List<T>();
        }

        public static void Return(List<T> list)
        {
            list.Clear();
            if (_bag.Count < DefaultMaxPerType) _bag.Add(list);
        }
    }

    // ── Dictionary<TKey, TValue> pool ──

    private static class DictPool<TKey, TValue> where TKey : notnull
    {
        private static readonly ConcurrentBag<Dictionary<TKey, TValue>> _bag = new();

        public static Dictionary<TKey, TValue> Rent(int capacity = 0)
        {
            if (_bag.TryTake(out var dict)) { dict.Clear(); return dict; }
            return capacity > 0 ? new Dictionary<TKey, TValue>(capacity) : new Dictionary<TKey, TValue>();
        }

        public static void Return(Dictionary<TKey, TValue> dict)
        {
            dict.Clear();
            if (_bag.Count < DefaultMaxPerType) _bag.Add(dict);
        }
    }

    // ── HashSet<T> pool ──

    private static class SetPool<T>
    {
        private static readonly ConcurrentBag<HashSet<T>> _bag = new();

        public static HashSet<T> Rent()
        {
            if (_bag.TryTake(out var set)) { set.Clear(); return set; }
            return new HashSet<T>();
        }

        public static void Return(HashSet<T> set)
        {
            set.Clear();
            if (_bag.Count < DefaultMaxPerType) _bag.Add(set);
        }
    }

    // ── StringBuilder pool ──

    private static class SbPool
    {
        private static readonly ConcurrentBag<System.Text.StringBuilder> _bag = new();

        public static System.Text.StringBuilder Rent()
        {
            if (_bag.TryTake(out var sb)) { sb.Clear(); return sb; }
            return new System.Text.StringBuilder(256);
        }

        public static void Return(System.Text.StringBuilder sb)
        {
            if (sb.Capacity > 4096) return; // Don't pool huge builders
            sb.Clear();
            if (_bag.Count < DefaultMaxPerType) _bag.Add(sb);
        }
    }

    // ── Public API ──

    public static List<T> RentList<T>(int capacity = 0) => ListPool<T>.Rent(capacity);
    public static void ReturnList<T>(List<T> list) => ListPool<T>.Return(list);

    public static Dictionary<TKey, TValue> RentDictionary<TKey, TValue>(int capacity = 0) where TKey : notnull
        => DictPool<TKey, TValue>.Rent(capacity);
    public static void ReturnDictionary<TKey, TValue>(Dictionary<TKey, TValue> dict) where TKey : notnull
        => DictPool<TKey, TValue>.Return(dict);

    public static HashSet<T> RentHashSet<T>() => SetPool<T>.Rent();
    public static void ReturnHashSet<T>(HashSet<T> set) => SetPool<T>.Return(set);

    public static System.Text.StringBuilder RentStringBuilder() => SbPool.Rent();
    public static void ReturnStringBuilder(System.Text.StringBuilder sb) => SbPool.Return(sb);
}
