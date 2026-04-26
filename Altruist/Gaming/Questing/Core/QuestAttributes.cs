using System.Security.Cryptography;
using System.Text;

namespace Altruist.Gaming.Questing;

[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestAttribute : Attribute
{
    public string Id { get; }
    public QuestAttribute(string id) => Id = id;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestIdAttribute : Attribute
{
    public string Id { get; }
    public QuestIdAttribute(string id) => Id = id;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestNameAttribute : Attribute
{
    public string Name { get; }
    public QuestNameAttribute(string name) => Name = name;
}

public enum QuestKind
{
    Script,
    Main,
    Side,
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestKindAttribute : Attribute
{
    public QuestKind Kind { get; }
    public QuestKindAttribute(QuestKind kind) => Kind = kind;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class QuestNpcAttribute : Attribute
{
    public string NpcKey { get; }
    public QuestNpcAttribute(string npcKey) => NpcKey = npcKey;
}

public enum QuestOperator
{
    Equals,
    NotEquals,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class QuestRequirementAttribute : Attribute
{
    public string Key { get; }
    public QuestOperator Operator { get; }
    public object Value { get; }

    public QuestRequirementAttribute(string key, QuestOperator op, int value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }

    public QuestRequirementAttribute(string key, QuestOperator op, long value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }

    public QuestRequirementAttribute(string key, QuestOperator op, double value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }

    public QuestRequirementAttribute(string key, QuestOperator op, string value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }

    public QuestRequirementAttribute(string key, QuestOperator op, bool value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class QuestStateAttribute : Attribute
{
    public string Name { get; }
    public bool Initial { get; set; }
    public QuestStateAttribute(string name) => Name = name;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class QuestStateEnterAttribute : Attribute
{
    public string StateName { get; }
    public QuestStateEnterAttribute(string stateName) => StateName = stateName;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class QuestStateExitAttribute : Attribute
{
    public string StateName { get; }
    public QuestStateExitAttribute(string stateName) => StateName = stateName;
}

public static class QuestId
{
    public static string Resolve(Type type)
    {
        var quest = type.GetCustomAttributes(typeof(QuestAttribute), false)
            .OfType<QuestAttribute>()
            .FirstOrDefault();
        if (quest != null && !string.IsNullOrWhiteSpace(quest.Id))
            return quest.Id;

        var id = type.GetCustomAttributes(typeof(QuestIdAttribute), false)
            .OfType<QuestIdAttribute>()
            .FirstOrDefault();
        if (id != null && !string.IsNullOrWhiteSpace(id.Id))
            return id.Id;

        var fullName = type.FullName ?? type.Name;
        if (fullName.Length <= 64)
            return fullName;

        var stem = type.Name.Length <= 32 ? type.Name : type.Name[..32];
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(fullName))).ToLowerInvariant();
        return $"{stem}_{hash[..24]}";
    }
}
