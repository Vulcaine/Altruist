using System.Numerics;
using System.Text.Json;
using Altruist;
using Altruist.Networking;
using MessagePack;

namespace Tests.Framework.Networking;

public class SnapshotSchemaTests
{
    public enum Side { Red, Blue }

    public sealed class Body
    {
        public Vector2 Position;
        public float Angle;
    }

    public sealed class Ship
    {
        public int Id;
        public Side Team;
        public Body Body = new();
        public bool Shielded;
        public float Heat;
        public int Kills;
    }

    public sealed class AnnotatedShip
    {
        [SnapshotField(0)] public int Id;
        [SnapshotField(1)] public Side Team { get; set; }
        [SnapshotField(2, Name = "hot")] public float Heat;
        [SnapshotField(3)] public bool Shielded;
        [SnapshotField(4, WhenMissing = 0)] public int Kills;
        public int NotSent;
    }

    private static readonly SnapshotRowSchema<Ship> ShipRow = SnapshotRowSchema.For<Ship>("ship")
        .Int("id", s => s.Id)
        .Enum("team", s => s.Team)
        .Number("x", s => s.Body.Position.X)
        .Number("y", s => s.Body.Position.Y)
        .Number("a", s => s.Body.Angle)
        .Bool("shielded", s => s.Shielded)
        .Number("heat", s => s.Heat)
        .Int("kills", s => s.Kills, whenMissing: 0)
        .Build();

    private static Ship Sample() => new()
    {
        Id = 7,
        Team = Side.Blue,
        Body = new Body { Position = new Vector2(1.5f, -2.25f), Angle = 0.1f },
        Shielded = true,
        Heat = 1f / 3f,
        Kills = 16_777_216,
    };

    [Fact]
    public void Encode_writes_the_same_floats_as_hand_written_casts()
    {
        var s = Sample();
        float[] hand = { s.Id, (float)s.Team, s.Body.Position.X, s.Body.Position.Y, s.Body.Angle, s.Shielded ? 1 : 0, s.Heat, s.Kills };
        var row = ShipRow.Encode(s);
        Assert.Equal(hand.Length, ShipRow.Length);
        Assert.Equal(hand.Select(BitConverter.SingleToInt32Bits), row.Select(BitConverter.SingleToInt32Bits));
        // ... and therefore the same MessagePack bytes.
        Assert.Equal(MessagePackSerializer.Serialize(hand), MessagePackSerializer.Serialize(row));
    }

    [Fact]
    public void Encode_into_a_buffer_at_an_offset_and_rejects_short_buffers()
    {
        var buffer = new float[ShipRow.Length + 3];
        ShipRow.Encode(Sample(), buffer, 2);
        Assert.Equal(0f, buffer[1]);
        Assert.Equal(7f, buffer[2]);
        Assert.Equal(1f, buffer[3]);
        Assert.Equal(0f, buffer[^1]);
        Assert.Throws<ArgumentException>(() => ShipRow.Encode(Sample(), new float[ShipRow.Length], 1));
        Assert.Throws<ArgumentException>(() => ShipRow.Encode(Sample(), new float[2], 5));
    }

    [Fact]
    public void Encode_into_a_buffer_allocates_nothing()
    {
        var s = Sample();
        var buffer = new float[ShipRow.Length];
        for (var i = 0; i < 1000; i++) ShipRow.Encode(s, buffer);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++) ShipRow.Encode(s, buffer);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Fields_describe_the_row()
    {
        Assert.Equal(new[] { "id", "team", "x", "y", "a", "shielded", "heat", "kills" }, ShipRow.Fields.Select(f => f.Name));
        Assert.Equal(Enumerable.Range(0, 8), ShipRow.Fields.Select(f => f.Index));
        var team = ShipRow.Fields[1];
        Assert.Equal(SnapshotFieldKind.Enum, team.Kind);
        Assert.Equal(new[] { "red", "blue" }, team.Labels);
        Assert.Null(team.WhenMissing);
        Assert.Equal(0f, ShipRow.Fields[^1].WhenMissing);
        Assert.Equal(typeof(Ship), ((ISnapshotRowSchema)ShipRow).Source);
    }

    [Fact]
    public void Optional_fields_must_be_trailing_and_names_unique()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SnapshotRowSchema.For<Ship>("s").Int("kills", s => s.Kills, whenMissing: 0).Int("id", s => s.Id));
        Assert.Contains("trailing", ex.Message);
        Assert.Throws<ArgumentException>(() => SnapshotRowSchema.For<Ship>("s").Int("id", s => s.Id).Int("id", s => s.Kills));
        Assert.Throws<ArgumentException>(() => SnapshotRowSchema.For<Ship>(" "));
    }

    [Fact]
    public void FromAttributes_builds_the_row_from_annotated_members()
    {
        var schema = SnapshotRowSchema.FromAttributes<AnnotatedShip>("ship");
        Assert.Equal(new[] { "id", "team", "hot", "shielded", "kills" }, schema.Fields.Select(f => f.Name));
        Assert.Equal(new[] { SnapshotFieldKind.Int, SnapshotFieldKind.Enum, SnapshotFieldKind.Number, SnapshotFieldKind.Bool, SnapshotFieldKind.Int }, schema.Fields.Select(f => f.Kind));
        Assert.Equal(0f, schema.Fields[4].WhenMissing);
        var row = schema.Encode(new AnnotatedShip { Id = 3, Team = Side.Blue, Heat = 0.5f, Shielded = true, Kills = 2, NotSent = 99 });
        Assert.Equal(new[] { 3f, 1f, 0.5f, 1f, 2f }, row);
    }

    public sealed class Gappy
    {
        [SnapshotField(0)] public int A;
        [SnapshotField(2)] public int B;
    }

    public sealed class Unsupported
    {
        [SnapshotField(0)] public string Name = "";
    }

    [Fact]
    public void FromAttributes_rejects_gaps_and_unsupported_types()
    {
        Assert.Throws<InvalidOperationException>(() => SnapshotRowSchema.FromAttributes<Gappy>("g"));
        Assert.Throws<NotSupportedException>(() => SnapshotRowSchema.FromAttributes<Unsupported>("u"));
    }

    [MessagePackObject]
    public class StatePacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 3001;
        [Key(1)] public int Tick { get; set; }
        [Key(2), WireField(Rows = "ship")] public float[][] Ships { get; set; } = Array.Empty<float[]>();
        [Key(3), WireField(Name = "wave", WhenMissing = 0)] public int WaveNumber { get; set; }
        [Key(4), WireField(WhenMissing = new float[0])] public float[] Heat { get; set; } = Array.Empty<float>();
    }

    [MessagePackObject]
    public class BadPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 1;
        [Key(1), WireField(Row = "nope")] public float[] Thing { get; set; } = Array.Empty<float>();
    }

    [MessagePackObject]
    public class WrongTypePacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 1;
        [Key(1), WireField(Row = "ship")] public float[][] Thing { get; set; } = Array.Empty<float[]>();
    }

    [Fact]
    public void ToJson_exports_rows_and_packets()
    {
        var json = new SnapshotSchemaSet().Add(ShipRow).AddPacket<StatePacket>("state").ToJson();
        Assert.EndsWith("}\n", json);
        Assert.DoesNotContain("\r", json);
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal(SnapshotSchemaSet.Format, doc.GetProperty("format").GetString());
        var ship = doc.GetProperty("rows").GetProperty("ship");
        Assert.Equal(8, ship.GetProperty("length").GetInt32());
        var team = ship.GetProperty("fields")[1];
        Assert.Equal("enum", team.GetProperty("kind").GetString());
        Assert.Equal("blue", team.GetProperty("labels")[1].GetString());
        Assert.False(team.TryGetProperty("whenMissing", out _));
        Assert.Equal(0, ship.GetProperty("fields")[7].GetProperty("whenMissing").GetSingle());

        var state = doc.GetProperty("packets").GetProperty("state");
        Assert.Equal(3001, state.GetProperty("code").GetInt32());
        var fields = state.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(new[] { "tick", "ships", "wave", "heat" }, fields.Select(f => f.GetProperty("name").GetString()));
        Assert.Equal(new[] { 1, 2, 3, 4 }, fields.Select(f => f.GetProperty("index").GetInt32()));
        Assert.Equal("rows", fields[1].GetProperty("kind").GetString());
        Assert.Equal("ship", fields[1].GetProperty("row").GetString());
        Assert.Equal(0, fields[2].GetProperty("whenMissing").GetInt32());
        Assert.Equal(JsonValueKind.Array, fields[3].GetProperty("whenMissing").ValueKind);
        Assert.False(fields[0].TryGetProperty("whenMissing", out _));
    }

    [Fact]
    public void ToJson_rejects_packets_that_refer_to_unknown_rows_or_hold_the_wrong_type()
    {
        Assert.Throws<InvalidOperationException>(() => new SnapshotSchemaSet().Add(ShipRow).AddPacket<BadPacket>("bad").ToJson());
        Assert.Throws<InvalidOperationException>(() => new SnapshotSchemaSet().Add(ShipRow).AddPacket<WrongTypePacket>("bad").ToJson());
        Assert.Throws<ArgumentException>(() => new SnapshotSchemaSet().Add(ShipRow).Add(ShipRow));
    }

    [Fact]
    public void Write_and_IsCurrent_guard_a_committed_file()
    {
        var set = new SnapshotSchemaSet().Add(ShipRow);
        var path = Path.Combine(Path.GetTempPath(), "altruist-snapshot-schema-" + Guid.NewGuid().ToString("N"), "schema.json");
        try
        {
            Assert.False(set.IsCurrent(path));
            set.Write(path);
            Assert.True(set.IsCurrent(path));
            File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));
            Assert.True(set.IsCurrent(path));
            Assert.False(new SnapshotSchemaSet().Add(ShipRow).Add(SnapshotRowSchema.FromAttributes<AnnotatedShip>("other")).IsCurrent(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void ToJson_is_deterministic()
    {
        string Make() => new SnapshotSchemaSet().Add(ShipRow).AddPacket<StatePacket>("state").ToJson();
        Assert.Equal(Make(), Make());
        Assert.Equal("{\n  \"format\": \"altruist.snapshot-schema/1\",\n  \"rows\": {},\n  \"packets\": {}\n}\n", new SnapshotSchemaSet().ToJson());
    }
}
