using System.Text.Json;

namespace Altruist.Gaming.Questing;

public interface IQuestState
{
    string CurrentState { get; set; }
    T Get<T>(string key, T defaultValue = default!);
    void Set<T>(string key, T value);
    int Increment(string key, int amount = 1);
    bool Has(string key);
    void Remove(string key);
    void Clear();
}

public sealed class QuestState : IQuestState
{
    private const string CurrentStateKey = "__state";
    private readonly Dictionary<string, JsonElement> _data = new();
    public bool IsDirty { get; private set; }

    public QuestState() { }

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

    public string CurrentState
    {
        get => Get(CurrentStateKey, "");
        set => Set(CurrentStateKey, value ?? "");
    }

    public void Set<T>(string key, T value)
    {
        var json = JsonSerializer.SerializeToElement(value);
        _data[key] = json;
        IsDirty = true;
    }

    public int Increment(string key, int amount = 1)
    {
        int current = Get(key, 0);
        int next = current + amount;
        Set(key, next);
        return next;
    }

    public bool Has(string key) => _data.ContainsKey(key);

    public void Remove(string key)
    {
        if (_data.Remove(key))
            IsDirty = true;
    }

    public void Clear()
    {
        _data.Clear();
        IsDirty = true;
    }

    public string Serialize() => JsonSerializer.Serialize(_data);
    public void MarkClean() => IsDirty = false;
}
