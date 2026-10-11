using System.Text.Json;

namespace Altruist.Gaming.Questing;

/// <summary>
/// Persisted key/value progress of one quest for one subject, exposed to handlers as
/// <see cref="QuestContext.State"/>. Values are stored as JSON, so use simple serializable
/// types (numbers, strings, bools, small records).
/// </summary>
/// <remarks>
/// Some keys have meaning to the runtime: <c>failed</c> / <c>done</c> / <c>_done</c> (bool) drive
/// <see cref="QuestStatus"/>; <c>_counter</c>, <c>target</c>/<c>target_count</c> and
/// <c>counter</c>/<c>kills</c>/<c>count</c> drive <see cref="QuestUpdate.CounterText"/>;
/// <c>__state</c> backs <see cref="CurrentState"/>; <c>__level_reconciled</c> is used by
/// <see cref="QuestRuntime{T}.ReconcileLevelAsync"/>.
/// </remarks>
public interface IQuestState
{
    /// <summary>Current state-machine state name (empty before the first state-machine dispatch or for hook-only quests). Setting it does not run enter/exit handlers.</summary>
    string CurrentState { get; set; }
    /// <summary>Reads a value, returning <paramref name="defaultValue"/> when the key is missing, null, or not convertible to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">State key (case-sensitive).</param>
    /// <param name="defaultValue">Fallback value.</param>
    /// <returns>The stored value or the fallback.</returns>
    T Get<T>(string key, T defaultValue = default!);
    /// <summary>Stores a value (JSON-serialized) and marks the state dirty.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="key">State key (case-sensitive).</param>
    /// <param name="value">Value to store.</param>
    void Set<T>(string key, T value);
    /// <summary>Adds <paramref name="amount"/> to an integer value (missing = 0) and stores it.</summary>
    /// <param name="key">State key.</param>
    /// <param name="amount">Amount to add (may be negative).</param>
    /// <returns>The new value.</returns>
    int Increment(string key, int amount = 1);
    /// <summary>Returns true when <paramref name="key"/> is stored.</summary>
    /// <param name="key">State key.</param>
    bool Has(string key);
    /// <summary>Removes <paramref name="key"/> if present.</summary>
    /// <param name="key">State key.</param>
    void Remove(string key);
    /// <summary>Removes every key, including <see cref="CurrentState"/> (restarts a state machine on next dispatch).</summary>
    void Clear();
}

/// <summary>
/// Default <see cref="IQuestState"/>: an in-memory JSON dictionary with dirty tracking, created
/// by the runtime for new quests and by an <see cref="IQuestStateStore"/> when loading. Not
/// thread-safe; one subject's quests are expected to be dispatched sequentially.
/// </summary>
public sealed class QuestState : IQuestState
{
    private const string CurrentStateKey = "__state";
    private readonly Dictionary<string, JsonElement> _data = new();
    /// <summary>True when the state changed since construction or the last <see cref="MarkClean"/>.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Creates an empty state.</summary>
    public QuestState() { }

    /// <summary>
    /// Restores a state from <see cref="Serialize"/> output. Null/blank or malformed JSON yields
    /// an empty state (errors are swallowed). The restored state is not dirty.
    /// </summary>
    /// <param name="json">JSON object produced by <see cref="Serialize"/>.</param>
    public QuestState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict != null)
                foreach (var kv in dict)
                    _data[kv.Key] = kv.Value;
        }
        catch
        {
        }
    }

    /// <inheritdoc/>
    public T Get<T>(string key, T defaultValue = default!)
    {
        if (!_data.TryGetValue(key, out var element))
            return defaultValue;

        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText()) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <inheritdoc/>
    public string CurrentState
    {
        get => Get(CurrentStateKey, "");
        set => Set(CurrentStateKey, value ?? "");
    }

    /// <inheritdoc/>
    public void Set<T>(string key, T value)
    {
        var json = JsonSerializer.SerializeToElement(value);
        _data[key] = json;
        IsDirty = true;
    }

    /// <inheritdoc/>
    public int Increment(string key, int amount = 1)
    {
        int current = Get(key, 0);
        int next = current + amount;
        Set(key, next);
        return next;
    }

    /// <inheritdoc/>
    public bool Has(string key) => _data.ContainsKey(key);

    /// <inheritdoc/>
    public void Remove(string key)
    {
        if (_data.Remove(key))
            IsDirty = true;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        _data.Clear();
        IsDirty = true;
    }

    /// <summary>Serializes all keys to a JSON object string for persistence (round-trips through <see cref="QuestState(string)"/>).</summary>
    /// <returns>JSON object text.</returns>
    public string Serialize() => JsonSerializer.Serialize(_data);
    /// <summary>Clears <see cref="IsDirty"/>; call after persisting. The runtime never calls it itself.</summary>
    public void MarkClean() => IsDirty = false;
}
