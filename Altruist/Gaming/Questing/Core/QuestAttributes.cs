using System.Security.Cryptography;
using System.Text;

namespace Altruist.Gaming.Questing;

/// <summary>
/// Declares a <see cref="QuestBehavior{T}"/> subclass as a quest with a stable, persisted
/// id. This is the entry point of attribute-based quest authoring: decorate a class with a
/// public parameterless constructor, implement hook interfaces (<see cref="IOnEnter{T}"/>,
/// <see cref="IOnKill{T}"/>, ...) or <see cref="QuestStateAttribute"/> methods, and
/// <see cref="QuestRuntime{T}.LoadFromAssembly"/> discovers it.
/// </summary>
/// <remarks>
/// <para>
/// The id is the key under which per-subject <see cref="QuestState"/> is stored by the
/// <see cref="IQuestStateStore"/>, so never change it once shipped. Id resolution order is
/// <see cref="QuestAttribute"/>, then <see cref="QuestIdAttribute"/>, then a name derived
/// from the type (see <see cref="QuestId.Resolve"/>); prefer setting it explicitly.
/// </para>
/// <para>
/// Companion attributes: <see cref="QuestNameAttribute"/> (display name),
/// <see cref="QuestKindAttribute"/> (category), <see cref="QuestNpcAttribute"/> (NPC routing),
/// <see cref="QuestRequirementAttribute"/> (gating). For one behavior class registered many
/// times with different data, use <see cref="QuestTemplateAttribute"/> plus an
/// <see cref="IQuestModule{T}"/> instead.
/// </para>
/// </remarks>
/// <example>
/// Interface-hook style (one handler per trigger, no explicit states):
/// <code>
/// [Quest("hunt.wolves")]
/// [QuestName("Wolf Hunt")]
/// [QuestKind(QuestKind.Side)]
/// [QuestRequirement("level", QuestOperator.GreaterOrEqual, 5)]
/// public sealed class WolfHunt : QuestBehavior&lt;MyQuestContext&gt;, IOnEnter&lt;MyQuestContext&gt;, IOnKill&lt;MyQuestContext&gt;
/// {
///     public Task OnEnter(MyQuestContext ctx)
///     {
///         if (!ctx.State.Has("target")) ctx.State.Set("target", 10); // counter text "0/10"
///         return Task.CompletedTask;
///     }
///
///     public Task OnKill(MyQuestContext ctx)
///     {
///         if (ctx.TargetId == WolfId &amp;&amp; ctx.State.Increment("counter") &gt;= 10)
///             ctx.State.Set("_done", true); // reported as QuestStatus.Completed
///         return Task.CompletedTask;
///     }
///
///     private const long WolfId = 101;
/// }
/// </code>
/// State-machine style (handlers scoped to the current state; the returned string is the next state):
/// <code>
/// [Quest("deliver.letter")]
/// [QuestNpc("postmaster")]
/// public sealed class DeliverLetter : QuestBehavior&lt;MyQuestContext&gt;
/// {
///     [QuestState("pickup", Initial = true)]
///     private string? OnNpc(MyQuestContext ctx) =&gt; "deliver";        // npc hook in "pickup"
///
///     [QuestStateEnter("deliver")]
///     private void GiveLetter(MyQuestContext ctx) =&gt; ctx.State.Set("has_letter", true);
///
///     [QuestState("deliver")]
///     private string? OnNpcDeliver(MyQuestContext ctx) =&gt; "complete"; // "complete" = Completed status
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestAttribute : Attribute
{
    /// <summary>The stable quest id (persistence key and <see cref="QuestUpdate.QuestId"/>).</summary>
    public string Id { get; }
    /// <summary>Declares the quest id.</summary>
    /// <param name="id">Stable, unique quest id; blank falls back to <see cref="QuestIdAttribute"/> or a type-derived id.</param>
    public QuestAttribute(string id) => Id = id;
}

/// <summary>
/// Alternative way to set a quest's id; consulted only when no non-blank
/// <see cref="QuestAttribute"/> is present (see <see cref="QuestId.Resolve"/>). Prefer
/// <see cref="QuestAttribute"/> for new quests.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestIdAttribute : Attribute
{
    /// <summary>The stable quest id.</summary>
    public string Id { get; }
    /// <summary>Declares the quest id.</summary>
    /// <param name="id">Stable, unique quest id.</param>
    public QuestIdAttribute(string id) => Id = id;
}

/// <summary>
/// Sets the display name reported in <see cref="QuestUpdate.Name"/> and
/// <see cref="QuestDefinition{T}.Name"/>. Without it the CLR type name is used.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestNameAttribute : Attribute
{
    /// <summary>The display name.</summary>
    public string Name { get; }
    /// <summary>Declares the display name.</summary>
    /// <param name="name">Human-readable quest name.</param>
    public QuestNameAttribute(string name) => Name = name;
}

/// <summary>
/// Marks a <see cref="QuestBehavior{T}"/> subclass as a behavior template that
/// should NOT be auto-discovered as a single quest by
/// <see cref="QuestRuntime{T}.LoadFromAssembly"/>. Templates are intended to be
/// instantiated multiple times by an <see cref="IQuestModule{T}"/> — once per
/// tier / per data row — and registered via
/// <see cref="QuestRuntime{T}.Register"/>. Only the attribute on the type itself is
/// checked (not inherited).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestTemplateAttribute : Attribute { }

/// <summary>
/// Category of a quest. Stored as its <c>ToString()</c> in
/// <see cref="QuestDefinition{T}.Category"/> / <see cref="QuestUpdate.Category"/>; the
/// runtime itself does not treat kinds differently.
/// </summary>
public enum QuestKind
{
    /// <summary>Background / scripted logic not shown as a quest (default when no <see cref="QuestKindAttribute"/>).</summary>
    Script,
    /// <summary>Main storyline quest.</summary>
    Main,
    /// <summary>Optional side quest.</summary>
    Side,
}

/// <summary>
/// Sets the <see cref="QuestKind"/> of an auto-discovered quest. Defaults to
/// <see cref="QuestKind.Script"/> when absent.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class QuestKindAttribute : Attribute
{
    /// <summary>The quest category.</summary>
    public QuestKind Kind { get; }
    /// <summary>Declares the quest category.</summary>
    /// <param name="kind">The category.</param>
    public QuestKindAttribute(QuestKind kind) => Kind = kind;
}

/// <summary>
/// Binds a quest to an NPC key (repeatable). When
/// <see cref="QuestRuntime{T}.FireAsync"/> is called with <see cref="QuestTrigger.Npc"/> and
/// an <c>npcKey</c> that has bindings, only the bound quests receive the
/// <see cref="QuestHooks.Npc"/> hook instead of every quest. Keys are matched
/// case-insensitively; blank keys are ignored.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class QuestNpcAttribute : Attribute
{
    /// <summary>The NPC key this quest is bound to.</summary>
    public string NpcKey { get; }
    /// <summary>Binds the quest to an NPC key.</summary>
    /// <param name="npcKey">Game-defined NPC identifier (case-insensitive).</param>
    public QuestNpcAttribute(string npcKey) => NpcKey = npcKey;
}

/// <summary>
/// Comparison operator carried by a <see cref="QuestRequirement"/>. The runtime does not
/// apply it itself; the registered <see cref="IQuestRequirementEvaluator{T}"/> for the
/// requirement key interprets it.
/// </summary>
public enum QuestOperator
{
    /// <summary>Actual value equals the requirement value.</summary>
    Equals,
    /// <summary>Actual value differs from the requirement value.</summary>
    NotEquals,
    /// <summary>Actual value is strictly greater.</summary>
    Greater,
    /// <summary>Actual value is greater or equal.</summary>
    GreaterOrEqual,
    /// <summary>Actual value is strictly less.</summary>
    Less,
    /// <summary>Actual value is less or equal.</summary>
    LessOrEqual,
}

/// <summary>
/// Gates a quest behind a requirement (repeatable; all must pass). Before every hook
/// dispatch the runtime looks up an <see cref="IQuestRequirementEvaluator{T}"/> by
/// <see cref="Key"/> in the <see cref="QuestRequirementRegistry{T}"/>; if any requirement
/// fails (or has no evaluator, reason <c>"missing_evaluator"</c>) the hook is skipped and the
/// quest is published as <see cref="QuestStatus.Suspended"/>.
/// </summary>
/// <example>
/// <code>
/// [QuestRequirement("level", QuestOperator.GreaterOrEqual, 10)]
/// [QuestRequirement("faction", QuestOperator.Equals, "guild")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class QuestRequirementAttribute : Attribute
{
    /// <summary>Requirement key; selects the evaluator (case-insensitive).</summary>
    public string Key { get; }
    /// <summary>Comparison the evaluator should apply.</summary>
    public QuestOperator Operator { get; }
    /// <summary>Boxed comparison value (<see cref="int"/>, <see cref="long"/>, <see cref="double"/>, <see cref="string"/> or <see cref="bool"/>).</summary>
    public object Value { get; }

    /// <summary>Declares an integer requirement.</summary>
    /// <param name="key">Evaluator key.</param>
    /// <param name="op">Comparison operator.</param>
    /// <param name="value">Value to compare against.</param>
    public QuestRequirementAttribute(string key, QuestOperator op, int value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }

    /// <summary>Declares a 64-bit integer requirement.</summary>
    /// <param name="key">Evaluator key.</param>
    /// <param name="op">Comparison operator.</param>
    /// <param name="value">Value to compare against.</param>
    public QuestRequirementAttribute(string key, QuestOperator op, long value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }

    /// <summary>Declares a floating-point requirement.</summary>
    /// <param name="key">Evaluator key.</param>
    /// <param name="op">Comparison operator.</param>
    /// <param name="value">Value to compare against.</param>
    public QuestRequirementAttribute(string key, QuestOperator op, double value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }

    /// <summary>Declares a string requirement.</summary>
    /// <param name="key">Evaluator key.</param>
    /// <param name="op">Comparison operator.</param>
    /// <param name="value">Value to compare against.</param>
    public QuestRequirementAttribute(string key, QuestOperator op, string value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }

    /// <summary>Declares a boolean requirement.</summary>
    /// <param name="key">Evaluator key.</param>
    /// <param name="op">Comparison operator.</param>
    /// <param name="value">Value to compare against.</param>
    public QuestRequirementAttribute(string key, QuestOperator op, bool value)
    {
        Key = key;
        Operator = op;
        Value = value;
    }
}

/// <summary>
/// Marks an instance method (public or private) of a quest behavior as a handler of the named
/// state (repeatable). Use it for multi-step quests where the same trigger means different
/// things per step; for single-step quests the hook interfaces (<see cref="IOnKill{T}"/> etc.)
/// are simpler.
/// </summary>
/// <remarks>
/// <para>
/// The method must take exactly one parameter assignable from the context type and return
/// <c>string?</c>, <c>Task&lt;string?&gt;</c> or <c>ValueTask&lt;string?&gt;</c>. The return
/// value is the next state; <c>null</c>, blank, or the current state means "stay". Returning a
/// name that has no <see cref="QuestStateAttribute"/> handlers ends the machine: the state is
/// stored and <c>_done</c> is set (reported as <see cref="QuestStatus.Completed"/>).
/// </para>
/// <para>
/// The hook the method handles is derived from its name: <c>OnEnter</c>, <c>OnLeave</c>,
/// <c>OnLevel</c>, <c>OnKill</c>, <c>OnNpc</c>, <c>OnItem</c>, <c>OnButton</c>,
/// <c>OnTimer</c> (also as prefixes, e.g. <c>OnNpcReturn</c> handles <c>npc</c>); any other
/// <c>OnXxx</c> handles custom hook <c>"xxx"</c> (first letter lower-cased); a name not
/// starting with <c>On</c> is a wildcard handler for every hook in that state. A state's
/// specific handler wins over its wildcard. If the current state has no handler for a hook,
/// the quest's hook-interface handler (if any) runs instead.
/// </para>
/// <para>
/// The initial state is the one flagged <see cref="Initial"/> (last flagged wins), otherwise
/// the first declared state in reflection order. It is entered lazily on the first dispatch
/// (firing its <see cref="QuestStateEnterAttribute"/> handler).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [QuestState("hunt", Initial = true)]
/// private string? OnKill(MyQuestContext ctx) =&gt; ctx.State.Increment("counter") &gt;= 5 ? "report" : null;
///
/// [QuestState("report")]
/// private Task&lt;string?&gt; OnNpc(MyQuestContext ctx) =&gt; Task.FromResult&lt;string?&gt;("complete");
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class QuestStateAttribute : Attribute
{
    /// <summary>State name (case-sensitive), persisted as <see cref="IQuestState.CurrentState"/>.</summary>
    public string Name { get; }
    /// <summary>When true, this state is the starting state of the machine.</summary>
    public bool Initial { get; set; }
    /// <summary>Declares a state handler.</summary>
    /// <param name="name">State name (case-sensitive).</param>
    public QuestStateAttribute(string name) => Name = name;
}

/// <summary>
/// Marks a method that runs when the quest state machine enters the named state (including
/// the lazy entry into the initial state). The method takes one context parameter and returns
/// <c>void</c>, <c>Task</c> or <c>ValueTask</c>. Use it for one-off setup of a step (granting
/// items, setting counters); per-trigger logic belongs in <see cref="QuestStateAttribute"/>
/// handlers. One enter handler per state (last declared wins).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class QuestStateEnterAttribute : Attribute
{
    /// <summary>The state whose entry triggers the method.</summary>
    public string StateName { get; }
    /// <summary>Declares a state-entry handler.</summary>
    /// <param name="stateName">State name (case-sensitive).</param>
    public QuestStateEnterAttribute(string stateName) => StateName = stateName;
}

/// <summary>
/// Marks a method that runs when the quest state machine leaves the named state, before the
/// new state is stored. Same signature rules as <see cref="QuestStateEnterAttribute"/>. One
/// exit handler per state (last declared wins).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class QuestStateExitAttribute : Attribute
{
    /// <summary>The state whose exit triggers the method.</summary>
    public string StateName { get; }
    /// <summary>Declares a state-exit handler.</summary>
    /// <param name="stateName">State name (case-sensitive).</param>
    public QuestStateExitAttribute(string stateName) => StateName = stateName;
}

/// <summary>Resolves the stable quest id of an attribute-authored quest type.</summary>
public static class QuestId
{
    /// <summary>
    /// Returns the quest id of <paramref name="type"/>: a non-blank <see cref="QuestAttribute"/>
    /// id, else a non-blank <see cref="QuestIdAttribute"/> id, else the type's full name when it
    /// is at most 64 characters, else <c>{first 32 chars of type name}_{24 hex chars of SHA-1 of
    /// the full name}</c>. Deterministic, but the fallbacks change when the type is renamed or
    /// moved, so declare ids explicitly for persisted quests.
    /// </summary>
    /// <param name="type">The quest behavior type.</param>
    /// <returns>The quest id.</returns>
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
