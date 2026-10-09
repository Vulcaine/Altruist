using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Altruist.Networking;

/// <summary>
/// How a reader interprets one slot of a snapshot row. Every kind travels as a 32-bit float on the
/// wire (MessagePack <c>float32</c>); the kind only tells decoders (the C# side and the
/// <c>@altruist/net</c> TypeScript twin) what to turn the number back into.
/// </summary>
public enum SnapshotFieldKind
{
    /// <summary>A float value, passed through as is.</summary>
    Number,
    /// <summary>An integral value (counters, ids, bitmasks). Exact on the wire up to ±2^24 (float32 mantissa).</summary>
    Int,
    /// <summary>A flag written as 1 / 0; decoders accept 1 or <c>true</c> as true.</summary>
    Bool,
    /// <summary>An enum written as its integral value; decoders map it to <see cref="SnapshotField.Labels"/> (and <see cref="SnapshotField.Fallback"/> when out of range).</summary>
    Enum,
}

/// <summary>
/// One slot of a <see cref="SnapshotRowSchema{T}"/>: its wire name, position and kind. Read it to
/// document or export a schema; it is created by <see cref="SnapshotRowBuilder{T}"/> or
/// <see cref="SnapshotFieldAttribute"/>, never by hand.
/// </summary>
/// <param name="Name">Wire name (the property name a decoder produces), camelCase by convention.</param>
/// <param name="Index">Position in the row (0-based, contiguous).</param>
/// <param name="Kind">How a decoder interprets the number.</param>
/// <param name="WhenMissing">
/// Value a decoder uses when a row from an older writer ends before this slot (or holds nil);
/// null = the field is required (rows always carry it). Optional fields are trailing: once a field
/// has one, every later field must too.
/// </param>
/// <param name="Labels">Enum labels by integral value (<see cref="SnapshotFieldKind.Enum"/> only).</param>
/// <param name="Fallback">Label a decoder uses for an out-of-range enum value; null = undefined / null.</param>
public sealed record SnapshotField(string Name, int Index, SnapshotFieldKind Kind, float? WhenMissing, IReadOnlyList<string>? Labels, string? Fallback);

/// <summary>Non-generic view of a <see cref="SnapshotRowSchema{T}"/> (export, validation, tooling).</summary>
public interface ISnapshotRowSchema
{
    /// <summary>Schema name, unique in a <see cref="SnapshotSchemaSet"/> (e.g. <c>"player"</c>); packets refer to rows by it.</summary>
    string Name { get; }

    /// <summary>The slots in wire order.</summary>
    IReadOnlyList<SnapshotField> Fields { get; }

    /// <summary>Number of floats one encoded row holds (<c>Fields.Count</c>).</summary>
    int Length { get; }

    /// <summary>The state type a row is read from.</summary>
    Type Source { get; }
}

/// <summary>
/// A declarative, positional snapshot row: the ordered list of values a server writes for one
/// object of type <typeparamref name="T"/> (a player, a ball, the match clock...) as a
/// <c>float[]</c>, which MessagePack sends as a compact array (no names on the wire). The same
/// declaration exports to JSON (<see cref="SnapshotSchemaSet.ToJson"/>) so a client decodes rows by
/// name with <c>@altruist/net</c> instead of hand-written indexes: a field is declared once.
/// <para>
/// Encoding runs a delegate compiled once from the field expressions (no reflection, no boxing per
/// call). <see cref="Encode(T, float[], int)"/> allocates nothing; <see cref="Encode(T)"/> allocates
/// only the row. It is a pure read of <typeparamref name="T"/>: deterministic, same bits as writing
/// the casts by hand.
/// </para>
/// <para>
/// Use it for state that is sent often as numbers (per-tick snapshots of many objects). Use a plain
/// MessagePack packet with <c>[Key]</c> members for rare, mixed-type messages (strings, nested
/// objects), and Altruist's <see cref="SyncedAttribute"/> entity sync for change-tracked world
/// entities where only changed properties should travel.
/// </para>
/// <para>
/// Compatibility: append new fields at the end with a <c>whenMissing</c> value so older writers'
/// shorter rows still decode; never reorder or remove fields (indexes are the wire contract).
/// </para>
/// </summary>
/// <example>
/// <code>
/// public static readonly SnapshotRowSchema&lt;Ship&gt; Ship = SnapshotRowSchema.For&lt;Ship&gt;("ship")
///     .Int("id", s =&gt; s.Id)
///     .Enum("team", s =&gt; s.Team)                 // labels: camelCase enum names
///     .Number("x", s =&gt; s.Body.Position.X)       // any expression, not only members
///     .Bool("shielded", s =&gt; s.Shielded)
///     .Int("kills", s =&gt; s.Kills, whenMissing: 0) // added later: old servers omit it
///     .Build();
///
/// float[] row = Ship.Encode(ship);                // or Ship.Encode(ship, buffer, offset)
/// </code>
/// </example>
public sealed class SnapshotRowSchema<T> : ISnapshotRowSchema
{
    private readonly Action<T, float[], int> _write;

    internal SnapshotRowSchema(string name, IReadOnlyList<SnapshotField> fields, Action<T, float[], int> write)
    {
        Name = name;
        Fields = fields;
        _write = write;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyList<SnapshotField> Fields { get; }

    /// <inheritdoc />
    public int Length => Fields.Count;

    /// <inheritdoc />
    public Type Source => typeof(T);

    /// <summary>
    /// Encodes <paramref name="source"/> into a new row of <see cref="Length"/> floats. Use it when the
    /// row must outlive the call (a packet shared by several receivers and encoded later by the
    /// outbox). To fill a pooled or packed buffer without allocating, use
    /// <see cref="Encode(T, float[], int)"/>.
    /// </summary>
    public float[] Encode(T source)
    {
        var row = new float[Fields.Count];
        _write(source, row, 0);
        return row;
    }

    /// <summary>
    /// Writes the row of <paramref name="source"/> into <paramref name="destination"/> starting at
    /// <paramref name="offset"/> (<see cref="Length"/> floats). Allocation-free: use it for pooled
    /// buffers or several rows packed in one array.
    /// </summary>
    /// <exception cref="ArgumentException">The destination is too short.</exception>
    public void Encode(T source, float[] destination, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if ((uint)offset > (uint)destination.Length || destination.Length - offset < Fields.Count)
            throw new ArgumentException($"snapshot row '{Name}' needs {Fields.Count} floats at offset {offset}; the destination holds {destination.Length}", nameof(destination));
        _write(source, destination, offset);
    }
}

/// <summary>
/// Entry points for <see cref="SnapshotRowSchema{T}"/>: <see cref="For{T}"/> declares a row in code
/// (computed values, several views of one type, state classes you do not want to annotate);
/// <see cref="FromAttributes{T}"/> builds one from <see cref="SnapshotFieldAttribute"/>s on the
/// state class itself (plain fields / properties, the declaration sits next to the data).
/// </summary>
public static class SnapshotRowSchema
{
    /// <summary>
    /// Starts a code-declared row named <paramref name="name"/>; fields get indexes in call order.
    /// Prefer it over <see cref="FromAttributes{T}"/> when values are computed (e.g. a physics body's
    /// position) or the state type lives in a project that should not know about the wire.
    /// </summary>
    public static SnapshotRowBuilder<T> For<T>(string name) => new(name);

    /// <summary>
    /// Builds a row from the <see cref="SnapshotFieldAttribute"/>s on <typeparamref name="T"/>'s
    /// public fields and properties (indexes from the attributes; they must be 0..n-1 without gaps).
    /// Kinds follow the member types: <c>bool</c> → <see cref="SnapshotFieldKind.Bool"/>, enums →
    /// <see cref="SnapshotFieldKind.Enum"/>, integral types → <see cref="SnapshotFieldKind.Int"/>,
    /// <c>float</c> / <c>double</c> → <see cref="SnapshotFieldKind.Number"/>. Use
    /// <see cref="For{T}"/> instead for computed values.
    /// </summary>
    /// <example>
    /// <code>
    /// public sealed class Ship
    /// {
    ///     [SnapshotField(0)] public int Id;
    ///     [SnapshotField(1)] public float Heat;
    ///     [SnapshotField(2, WhenMissing = 0)] public int Kills;   // appended later
    /// }
    /// var schema = SnapshotRowSchema.FromAttributes&lt;Ship&gt;("ship");
    /// </code>
    /// </example>
    public static SnapshotRowSchema<T> FromAttributes<T>(string name)
    {
        var members = typeof(T).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m is FieldInfo or PropertyInfo)
            .Select(m => (Member: m, Attr: m.GetCustomAttribute<SnapshotFieldAttribute>()))
            .Where(x => x.Attr is not null)
            .OrderBy(x => x.Attr!.Index)
            .ToList();
        var builder = new SnapshotRowBuilder<T>(name);
        var p = Expression.Parameter(typeof(T), "s");
        for (var i = 0; i < members.Count; i++)
        {
            var (member, attr) = members[i];
            if (attr!.Index != i)
                throw new InvalidOperationException($"{typeof(T).Name}: [SnapshotField] indexes must be 0..{members.Count - 1} without gaps or repeats; {member.Name} has {attr.Index}, expected {i}");
            Expression access = member is FieldInfo f ? Expression.Field(p, f) : Expression.Property(p, (PropertyInfo)member);
            var type = access.Type;
            var wireName = attr.Name ?? CamelCase(member.Name);
            float? whenMissing = float.IsNaN(attr.WhenMissing) ? null : attr.WhenMissing;
            if (type == typeof(bool)) builder.Add(wireName, SnapshotFieldKind.Bool, access, p, whenMissing, null, null);
            else if (type.IsEnum) builder.Add(wireName, SnapshotFieldKind.Enum, access, p, whenMissing, EnumLabels(type), attr.Fallback);
            else if (type == typeof(float) || type == typeof(double)) builder.Add(wireName, SnapshotFieldKind.Number, access, p, whenMissing, null, null);
            else if (type == typeof(int) || type == typeof(uint) || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(long) || type == typeof(ulong))
                builder.Add(wireName, SnapshotFieldKind.Int, access, p, whenMissing, null, null);
            else throw new NotSupportedException($"{typeof(T).Name}.{member.Name}: [SnapshotField] supports bool, enums, integral and floating-point members, not {type.Name}");
        }
        return builder.Build();
    }

    /// <summary>camelCase of a member name (<c>DashTime</c> → <c>dashTime</c>), the default wire name.</summary>
    public static string CamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>Default enum labels: camelCase names by value; values must be 0..n-1.</summary>
    internal static string[] EnumLabels(Type enumType)
    {
        var values = Enum.GetValues(enumType).Cast<object>().Select(v => (Value: Convert.ToInt64(v), Name: CamelCase(Enum.GetName(enumType, v)!)))
            .OrderBy(v => v.Value).ToArray();
        for (var i = 0; i < values.Length; i++)
            if (values[i].Value != i)
                throw new NotSupportedException($"{enumType.Name}: default snapshot labels need enum values 0..n-1; pass labels explicitly");
        return values.Select(v => v.Name).ToArray();
    }
}

/// <summary>
/// Marks a public field or property as one slot of a positional snapshot row built with
/// <see cref="SnapshotRowSchema.FromAttributes{T}"/>. Use it on plain state classes whose members
/// are the values to send; use <see cref="SnapshotRowSchema.For{T}"/> for computed values.
/// Unrelated to <see cref="SyncedAttribute"/> (named, change-tracked entity sync).
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class SnapshotFieldAttribute : Attribute
{
    /// <summary>Marks the member as slot <paramref name="index"/> (0-based, contiguous over the type).</summary>
    public SnapshotFieldAttribute(int index) => Index = index;

    /// <summary>Position in the row.</summary>
    public int Index { get; }

    /// <summary>Wire name; default the camelCase member name.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Value decoders use when an older writer's row ends before this slot; <c>NaN</c> (default) =
    /// required. Set it on fields appended after a release (see <see cref="SnapshotField.WhenMissing"/>).
    /// </summary>
    public float WhenMissing { get; set; } = float.NaN;

    /// <summary>Enum members only: label for out-of-range values (see <see cref="SnapshotField.Fallback"/>).</summary>
    public string? Fallback { get; set; }
}

/// <summary>
/// Declares the slots of a <see cref="SnapshotRowSchema{T}"/> in wire order (each call appends the
/// next index). Value expressions may be any read of the source (<c>s =&gt; s.Body.Position.X</c>);
/// they are inlined into one compiled writer by <see cref="Build"/>. Keep them side-effect free:
/// they run once per encoded row.
/// </summary>
public sealed class SnapshotRowBuilder<T>
{
    private readonly string _name;
    private readonly List<SnapshotField> _fields = new();
    private readonly List<Expression> _values = new();
    private readonly ParameterExpression _source = Expression.Parameter(typeof(T), "source");

    internal SnapshotRowBuilder(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("a snapshot row needs a name", nameof(name));
        _name = name;
    }

    /// <summary>
    /// Appends a float slot (<see cref="SnapshotFieldKind.Number"/>). <paramref name="whenMissing"/>:
    /// the value decoders use for rows of older writers that end before it (set it on appended fields).
    /// </summary>
    public SnapshotRowBuilder<T> Number(string name, Expression<Func<T, float>> value, float? whenMissing = null) =>
        Add(name, SnapshotFieldKind.Number, value, whenMissing, null, null);

    /// <summary>
    /// Appends an integral slot (<see cref="SnapshotFieldKind.Int"/>; ids, counters, bitmasks up to
    /// 24 bits). Written as <c>(float)value</c>.
    /// </summary>
    public SnapshotRowBuilder<T> Int(string name, Expression<Func<T, int>> value, float? whenMissing = null) =>
        Add(name, SnapshotFieldKind.Int, value, whenMissing, null, null);

    /// <summary>Appends a flag slot (<see cref="SnapshotFieldKind.Bool"/>), written as 1 / 0.</summary>
    public SnapshotRowBuilder<T> Bool(string name, Expression<Func<T, bool>> value, float? whenMissing = null) =>
        Add(name, SnapshotFieldKind.Bool, value, whenMissing, null, null);

    /// <summary>
    /// Appends an enum slot (<see cref="SnapshotFieldKind.Enum"/>) written as its integral value.
    /// <paramref name="labels"/> default to the camelCase enum names by value (values must be
    /// 0..n-1); <paramref name="fallback"/> is what decoders produce for an unknown value (default:
    /// nothing, i.e. undefined).
    /// </summary>
    public SnapshotRowBuilder<T> Enum<TEnum>(string name, Expression<Func<T, TEnum>> value, IReadOnlyList<string>? labels = null, string? fallback = null, float? whenMissing = null)
        where TEnum : struct, System.Enum =>
        Add(name, SnapshotFieldKind.Enum, value, whenMissing, labels ?? SnapshotRowSchema.EnumLabels(typeof(TEnum)), fallback);

    private SnapshotRowBuilder<T> Add(string name, SnapshotFieldKind kind, LambdaExpression value, float? whenMissing, IReadOnlyList<string>? labels, string? fallback)
    {
        ArgumentNullException.ThrowIfNull(value);
        var body = new Rebind(value.Parameters[0], _source).Visit(value.Body);
        return Add(name, kind, body, _source, whenMissing, labels, fallback);
    }

    internal SnapshotRowBuilder<T> Add(string name, SnapshotFieldKind kind, Expression body, ParameterExpression parameter, float? whenMissing, IReadOnlyList<string>? labels, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("a snapshot field needs a name", nameof(name));
        if (_fields.Any(f => f.Name == name))
            throw new ArgumentException($"snapshot row '{_name}' already has a field '{name}'", nameof(name));
        if (whenMissing is null && _fields.Count > 0 && _fields[^1].WhenMissing is not null)
            throw new InvalidOperationException($"snapshot row '{_name}': '{name}' follows optional field '{_fields[^1].Name}' and needs a whenMissing value too (optional fields are trailing)");
        if (parameter != _source)
            body = new Rebind(parameter, _source).Visit(body);
        _fields.Add(new SnapshotField(name, _fields.Count, kind, whenMissing, labels, fallback));
        _values.Add(ToFloat(body, kind));
        return this;
    }

    /// <summary>Compiles the writer and returns the immutable schema. Call once (static field) and reuse.</summary>
    public SnapshotRowSchema<T> Build()
    {
        var destination = Expression.Parameter(typeof(float[]), "destination");
        var offset = Expression.Parameter(typeof(int), "offset");
        var writes = new List<Expression>(_values.Count + 1);
        for (var i = 0; i < _values.Count; i++)
        {
            var index = i == 0 ? (Expression)offset : Expression.Add(offset, Expression.Constant(i));
            writes.Add(Expression.Assign(Expression.ArrayAccess(destination, index), _values[i]));
        }
        writes.Add(Expression.Empty());
        var lambda = Expression.Lambda<Action<T, float[], int>>(Expression.Block(writes), _source, destination, offset);
        return new SnapshotRowSchema<T>(_name, _fields.ToArray(), lambda.Compile());
    }

    private static Expression ToFloat(Expression value, SnapshotFieldKind kind)
    {
        if (kind == SnapshotFieldKind.Bool)
            return Expression.Condition(value, Expression.Constant(1f), Expression.Constant(0f));
        if (value.Type.IsEnum)
            value = Expression.Convert(value, System.Enum.GetUnderlyingType(value.Type));
        return value.Type == typeof(float) ? value : Expression.Convert(value, typeof(float));
    }

    private sealed class Rebind : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public Rebind(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : node;
    }
}
