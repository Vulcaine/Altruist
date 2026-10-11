using System.Reflection;
using System.Text;
using System.Text.Json;
using MessagePack;

namespace Altruist.Networking;

/// <summary>
/// Describes one member of a MessagePack packet (a <c>[Key(n)]</c> property) for the snapshot
/// schema export: what the member holds and what a decoder uses when an older writer's packet
/// array ends before it. Needed only on members that hold schema rows or that were appended after a
/// release; other <c>[Key]</c> members export as plain values under their camelCase name.
/// Changes nothing about how MessagePack encodes the packet.
/// </summary>
/// <example>
/// <code>
/// [MessagePackObject]
/// public class StatePacket : IPacketBase
/// {
///     [Key(0)] public uint MessageCode { get; set; } = 3001;
///     [Key(1)] public int Tick { get; set; }
///     [Key(2), WireField(Rows = "ship")] public float[][] Ships { get; set; } = [];
///     [Key(3), WireField(WhenMissing = 0)] public int Wave { get; set; }          // appended later
///     [Key(4), WireField(WhenMissing = new float[0])] public float[] Heat { get; set; } = [];
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class WireFieldAttribute : Attribute
{
    /// <summary>Wire name a decoder produces; default the camelCase member name.</summary>
    public string? Name { get; set; }

    /// <summary>The member is one row (<c>float[]</c>) of the named <see cref="ISnapshotRowSchema"/>; decoders turn it into an object.</summary>
    public string? Row { get; set; }

    /// <summary>The member is an array of rows (<c>float[][]</c>) of the named <see cref="ISnapshotRowSchema"/>; decoders turn it into an array of objects.</summary>
    public string? Rows { get; set; }

    /// <summary>
    /// Value decoders use when the packet array ends before this key or holds nil (number, bool,
    /// string, or an empty array such as <c>new float[0]</c>). Unset = the raw value (undefined /
    /// null) is passed through.
    /// </summary>
    public object? WhenMissing { get; set; }
}

/// <summary>
/// The snapshot rows and packets a server sends, exported as one JSON document (format
/// <see cref="Format"/>) that client decoders read (<c>@altruist/net</c>: <c>SnapshotSchema</c>).
/// Commit the JSON next to the client and guard it with a test (<see cref="IsCurrent"/>): the C#
/// declarations stay the single source, and a stale file fails the build instead of desyncing.
/// </summary>
/// <example>
/// <code>
/// public static readonly SnapshotSchemaSet Wire = new SnapshotSchemaSet()
///     .Add(Schemas.Ship)
///     .AddPacket&lt;StatePacket&gt;("state");
///
/// // test: fail when stale, rewrite on request
/// if (update) Wire.Write(path); else Assert.True(Wire.IsCurrent(path), "run with UPDATE=1");
/// </code>
/// </example>
public sealed class SnapshotSchemaSet
{
    /// <summary>Format tag written into the document (bumped on incompatible document changes).</summary>
    public const string Format = "altruist.snapshot-schema/1";

    private readonly List<ISnapshotRowSchema> _rows = new();
    private readonly List<(string Name, Type Type)> _packets = new();

    /// <summary>The registered rows, in registration order.</summary>
    public IReadOnlyList<ISnapshotRowSchema> Rows => _rows;

    /// <summary>Registers a row schema (names must be unique).</summary>
    public SnapshotSchemaSet Add(ISnapshotRowSchema row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_rows.Any(r => r.Name == row.Name))
            throw new ArgumentException($"snapshot row '{row.Name}' is already registered", nameof(row));
        _rows.Add(row);
        return this;
    }

    /// <summary>
    /// Registers a MessagePack packet type (<c>[MessagePackObject]</c> with <c>[Key(n)]</c> members)
    /// under <paramref name="name"/>. Its keys export in order with camelCase names (or
    /// <see cref="WireFieldAttribute"/> overrides); <see cref="IPacketBase.MessageCode"/> becomes the
    /// packet's <c>code</c> (read from a default instance) and is not a field.
    /// </summary>
    public SnapshotSchemaSet AddPacket<TPacket>(string name) where TPacket : new()
    {
        if (_packets.Any(p => p.Name == name))
            throw new ArgumentException($"packet '{name}' is already registered", nameof(name));
        _packets.Add((name, typeof(TPacket)));
        return this;
    }

    /// <summary>
    /// The JSON document: <c>{ format, rows: { name: { length, fields } }, packets: { name: { code, fields } } }</c>,
    /// one field per line, <c>\n</c> line ends and a final newline, so it diffs well when committed.
    /// Deterministic for the same declarations.
    /// </summary>
    /// <exception cref="InvalidOperationException">A packet refers to an unregistered row or has invalid keys.</exception>
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"format\": ").Append(Json(Format)).Append(",\n  \"rows\": {");
        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            sb.Append(r == 0 ? "\n" : ",\n").Append("    ").Append(Json(row.Name)).Append(": {\n      \"length\": ").Append(row.Length).Append(",\n      \"fields\": [");
            for (var i = 0; i < row.Fields.Count; i++)
            {
                var f = row.Fields[i];
                var o = new Dictionary<string, object?> { ["name"] = f.Name, ["index"] = f.Index, ["kind"] = f.Kind.ToString().ToLowerInvariant() };
                if (f.WhenMissing is { } wm) o["whenMissing"] = wm;
                if (f.Labels is not null) o["labels"] = f.Labels;
                if (f.Fallback is not null) o["fallback"] = f.Fallback;
                sb.Append(i == 0 ? "\n" : ",\n").Append("        ").Append(Json(o));
            }
            sb.Append("\n      ]\n    }");
        }
        sb.Append(_rows.Count == 0 ? "},\n" : "\n  },\n").Append("  \"packets\": {");
        for (var p = 0; p < _packets.Count; p++)
        {
            var (name, type) = _packets[p];
            var code = Activator.CreateInstance(type) is IPacketBase pb ? (object)pb.MessageCode : null;
            sb.Append(p == 0 ? "\n" : ",\n").Append("    ").Append(Json(name)).Append(": {\n      \"code\": ").Append(Json(code)).Append(",\n      \"fields\": [");
            var fields = PacketFields(type);
            for (var i = 0; i < fields.Count; i++)
                sb.Append(i == 0 ? "\n" : ",\n").Append("        ").Append(Json(fields[i]));
            sb.Append("\n      ]\n    }");
        }
        sb.Append(_packets.Count == 0 ? "}\n}\n" : "\n  }\n}\n");
        return sb.ToString();
    }

    /// <summary>True when the file at <paramref name="path"/> holds exactly <see cref="ToJson"/> (line ends normalized).</summary>
    public bool IsCurrent(string path) =>
        File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == ToJson();

    /// <summary>Writes <see cref="ToJson"/> to <paramref name="path"/> (creating the directory); the update path of a staleness test.</summary>
    public void Write(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    private List<Dictionary<string, object?>> PacketFields(Type type)
    {
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m is PropertyInfo or FieldInfo)
            .Select(m => (Member: m, Key: m.GetCustomAttribute<KeyAttribute>()))
            .Where(x => x.Key is not null)
            .ToList();
        if (members.Any(x => x.Key!.IntKey is null))
            throw new InvalidOperationException($"{type.Name}: snapshot schema export needs integer [Key(n)] members (array packets)");
        var list = new List<Dictionary<string, object?>>();
        foreach (var (member, key) in members.OrderBy(x => x.Key!.IntKey))
        {
            if (member.Name == nameof(IPacketBase.MessageCode)) continue;
            var wire = member.GetCustomAttribute<WireFieldAttribute>();
            var o = new Dictionary<string, object?>
            {
                ["name"] = wire?.Name ?? SnapshotRowSchema.CamelCase(member.Name),
                ["index"] = key!.IntKey,
            };
            var memberType = member is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)member).FieldType;
            if (wire?.Row is { } row)
            {
                Require(type, member, row, memberType, typeof(float[]));
                o["kind"] = "row";
                o["row"] = row;
            }
            else if (wire?.Rows is { } rows)
            {
                Require(type, member, rows, memberType, typeof(float[][]));
                o["kind"] = "rows";
                o["row"] = rows;
            }
            else o["kind"] = "value";
            if (wire?.WhenMissing is { } wm) o["whenMissing"] = wm;
            list.Add(o);
        }
        return list;
    }

    private void Require(Type packet, MemberInfo member, string row, Type actual, Type expected)
    {
        if (_rows.All(r => r.Name != row))
            throw new InvalidOperationException($"{packet.Name}.{member.Name} refers to snapshot row '{row}', which is not registered in this set");
        if (actual != expected)
            throw new InvalidOperationException($"{packet.Name}.{member.Name} holds rows of '{row}' and must be {expected.Name}, not {actual.Name}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);
}
