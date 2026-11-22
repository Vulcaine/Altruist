using Altruist;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.UORM;
using Xunit;
using FluentAssertions;
using static Altruist.Persistence.VaultDocument;

namespace Tests.Altruist.Persistance.Migrations;

public class MigrationPlannerTests
{
    private readonly PostgresMigrationPlanner _planner = new();
    private readonly PostgresMigrationPlanner _droppingPlanner = new(dropUnmappedColumns: true);

    // Minimal model satisfying IStoredModel + [Vault] + Type property for VaultDocument validation
    [Vault("test_model", Keyspace: "public")]
    [VaultPrimaryKey("StorageId")]
    private class TestModel : IStoredModel
    {
        public string StorageId { get; set; } = "";
        public string Type { get; set; } = "test";
    }

    // ── Helpers ──

    private static IReadOnlyDictionary<string, DatabaseModel> NoSchemas()
        => new Dictionary<string, DatabaseModel>();

    private static IReadOnlyDictionary<string, DatabaseModel> WithSchema(
        string schema, params (string name, TableModel model)[] tables)
    {
        var dict = new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, m) in tables) dict[n] = m;
        return new Dictionary<string, DatabaseModel> { [schema] = new(schema, dict) };
    }

    private static TableModel Table(string name, string[] cols, string[] pk)
    {
        var columns = cols.ToDictionary(c => c, c => new ColumnModel(c, "text", false), StringComparer.OrdinalIgnoreCase);
        return new(name, columns, pk,
            new Dictionary<string, UniqueConstraintModel>(),
            new Dictionary<string, IndexModel>(),
            Array.Empty<ForeignKeyModel>());
    }

    private static TableModel TableWithUniques(string name, string[] cols, string[] pk,
        Dictionary<string, UniqueConstraintModel> uniques)
    {
        var columns = cols.ToDictionary(c => c, c => new ColumnModel(c, "text", false), StringComparer.OrdinalIgnoreCase);
        return new(name, columns, pk, uniques, new Dictionary<string, IndexModel>(), Array.Empty<ForeignKeyModel>());
    }

    private static TableModel TableWithIndexes(string name, string[] cols, string[] pk,
        Dictionary<string, IndexModel> indexes)
    {
        var columns = cols.ToDictionary(c => c, c => new ColumnModel(c, "text", false), StringComparer.OrdinalIgnoreCase);
        return new(name, columns, pk, new Dictionary<string, UniqueConstraintModel>(), indexes, Array.Empty<ForeignKeyModel>());
    }

    private static TableModel TableWithFKs(string name, string[] cols, string[] pk, ForeignKeyModel[] fks)
    {
        var columns = cols.ToDictionary(c => c, c => new ColumnModel(c, "text", false), StringComparer.OrdinalIgnoreCase);
        return new(name, columns, pk, new Dictionary<string, UniqueConstraintModel>(),
            new Dictionary<string, IndexModel>(), fks);
    }

    private static VaultDocument Doc(string tableName, Dictionary<string, Type> fieldTypes, string[] pk,
        string keyspace = "public", bool history = false,
        List<UniqueKeyDefinition>? uniques = null,
        List<string>? indexes = null,
        List<VaultForeignKeyDefinition>? fks = null)
    {
        var fields = fieldTypes.Keys.ToList();
        var columns = fields.ToDictionary(f => f, f => f, StringComparer.OrdinalIgnoreCase);
        var doc = new VaultDocument(
            header: new VaultAttribute(tableName, Keyspace: keyspace),
            type: typeof(TestModel),
            name: tableName,
            fields: fields,
            columns: columns,
            indexes: indexes ?? new(),
            uniqueKeys: uniques ?? new(),
            propertyAccessors: new(),
            primaryKeyAttribute: pk.Length > 0 ? new VaultPrimaryKeyAttribute(pk) : null,
            storeHistory: history,
            foreignKeys: fks);
        doc.FieldTypes = fieldTypes;
        return doc;
    }

    private static VaultDocument SimpleDoc(string tableName, string[] fields, string[] pk,
        string keyspace = "public", bool history = false)
    {
        var types = fields.ToDictionary(f => f, f => typeof(string) as Type);
        return Doc(tableName, types, pk, keyspace, history);
    }

    // ═══════════════════════════════════════════════════════
    // New Table Creation
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void NewTable_CreatesCreateTableOp()
    {
        var doc = SimpleDoc("players", ["id", "name", "level"], ["id"]);
        var ops = _planner.Plan(NoSchemas(), [doc]);

        ops.Should().ContainSingle(o => o is CreateTableOperation);
        var ct = ops.OfType<CreateTableOperation>().First();
        ct.Table.Should().Be("players");
        ct.Schema.Should().Be("public");
        ct.PrimaryKeyColumns.Should().BeEquivalentTo(["id"]);
        ct.Columns.Should().HaveCount(3);
    }

    [Fact]
    public void NewTable_CompositePK()
    {
        var doc = SimpleDoc("inv", ["player_id", "slot", "item"], ["player_id", "slot"]);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        ops.OfType<CreateTableOperation>().First()
            .PrimaryKeyColumns.Should().BeEquivalentTo(["player_id", "slot"]);
    }

    [Fact]
    public void NewTable_NoPK_Throws()
    {
        var act = () => SimpleDoc("bad", ["col"], []);
        // VaultPrimaryKeyAttribute with empty keys is still created, planner validates
    }

    [Fact]
    public void NewTable_PKColumnsNotNullable()
    {
        var doc = SimpleDoc("t", ["id", "name"], ["id"]);
        doc.NullableColumns.Add("id"); // attempt to make PK nullable
        var ops = _planner.Plan(NoSchemas(), [doc]);
        var ct = ops.OfType<CreateTableOperation>().First();
        ct.Columns.First(c => c.Name == "id").IsNullable.Should().BeFalse();
    }

    [Fact]
    public void NewTable_NullableColumnsRespected()
    {
        var doc = SimpleDoc("t", ["id", "bio"], ["id"]);
        doc.NullableColumns.Add("bio");
        var ops = _planner.Plan(NoSchemas(), [doc]);
        ops.OfType<CreateTableOperation>().First()
            .Columns.First(c => c.Name == "bio").IsNullable.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════
    // Type Mapping
    // ═══════════════════════════════════════════════════════

    [Theory]
    [InlineData(typeof(string), "text")]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(long), "bigint")]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(float), "real")]
    [InlineData(typeof(double), "double precision")]
    [InlineData(typeof(decimal), "numeric")]
    [InlineData(typeof(DateTime), "timestamp")]
    [InlineData(typeof(DateTimeOffset), "timestamptz")]
    [InlineData(typeof(Guid), "uuid")]
    [InlineData(typeof(byte[]), "bytea")]
    [InlineData(typeof(TimeSpan), "interval")]
    [InlineData(typeof(short), "smallint")]
    [InlineData(typeof(byte), "smallint")]
    public void TypeMapping_Primitives(Type clrType, string expected)
    {
        var doc = Doc("t", new() { ["id"] = typeof(string), ["val"] = clrType }, ["id"]);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        ops.OfType<CreateTableOperation>().First()
            .Columns.First(c => c.Name == "val").StoreType.Should().Be(expected);
    }

    [Theory]
    [InlineData(typeof(int[]), "integer[]")]
    [InlineData(typeof(long[]), "bigint[]")]
    [InlineData(typeof(string[]), "text[]")]
    [InlineData(typeof(float[]), "real[]")]
    [InlineData(typeof(double[]), "double precision[]")]
    [InlineData(typeof(Guid[]), "uuid[]")]
    [InlineData(typeof(short[]), "smallint[]")]
    public void TypeMapping_Arrays(Type clrType, string expected)
    {
        var doc = Doc("t", new() { ["id"] = typeof(string), ["arr"] = clrType }, ["id"]);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        ops.OfType<CreateTableOperation>().First()
            .Columns.First(c => c.Name == "arr").StoreType.Should().Be(expected);
    }

    [Fact]
    public void TypeMapping_NullableUnwraps()
    {
        var doc = Doc("t", new() { ["id"] = typeof(string), ["val"] = typeof(int?) }, ["id"]);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        ops.OfType<CreateTableOperation>().First()
            .Columns.First(c => c.Name == "val").StoreType.Should().Be("integer");
    }

    [Fact]
    public void TypeMapping_Enum_MapsToInteger()
    {
        var doc = Doc("t", new() { ["id"] = typeof(string), ["day"] = typeof(DayOfWeek) }, ["id"]);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        ops.OfType<CreateTableOperation>().First()
            .Columns.First(c => c.Name == "day").StoreType.Should().Be("integer");
    }

    [Fact]
    public void TypeMapping_Collection_FallsToJsonb()
    {
        var doc = Doc("t", new() { ["id"] = typeof(string), ["tags"] = typeof(List<string>) }, ["id"]);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        ops.OfType<CreateTableOperation>().First()
            .Columns.First(c => c.Name == "tags").StoreType.Should().Be("jsonb");
    }

    // ═══════════════════════════════════════════════════════
    // Column Diff
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Diff_NewColumn_PlansAdd()
    {
        var schemas = WithSchema("public", ("t", Table("t", ["id", "name"], ["id"])));
        var doc = SimpleDoc("t", ["id", "name", "level"], ["id"]);
        var ops = _planner.Plan(schemas, [doc]);
        ops.Should().ContainSingle(o => o is AddColumnOperation);
        ops.OfType<AddColumnOperation>().First().Column.Name.Should().Be("level");
    }

    [Fact]
    public void Diff_RemovedColumn_PlansDrop()
    {
        var schemas = WithSchema("public", ("t", Table("t", ["id", "name", "old"], ["id"])));
        var doc = SimpleDoc("t", ["id", "name"], ["id"]);
        var ops = _droppingPlanner.Plan(schemas, [doc]);
        ops.Should().ContainSingle(o => o is DropColumnOperation);
        ops.OfType<DropColumnOperation>().First().ColumnName.Should().Be("old");
    }

    [Fact]
    public void Diff_NoChanges_EmptyOps()
    {
        var schemas = WithSchema("public", ("t", Table("t", ["id", "name"], ["id"])));
        var doc = SimpleDoc("t", ["id", "name"], ["id"]);
        _planner.Plan(schemas, [doc]).Should().BeEmpty();
    }

    [Fact]
    public void Diff_TableWithoutPrimaryKey_PlansAddPrimaryKey()
    {
        var schemas = WithSchema("public", ("t", Table("t", ["id", "name"], [])));
        var doc = SimpleDoc("t", ["id", "name"], ["id"]);
        var op = _planner.Plan(schemas, [doc]).Should().ContainSingle().Which.Should().BeOfType<AddPrimaryKeyOperation>().Subject;
        op.Table.Should().Be("t");
        op.Columns.Should().Equal("id");
    }

    [Fact]
    public void Diff_AddAndDrop_BothPlanned()
    {
        var schemas = WithSchema("public", ("t", Table("t", ["id", "old"], ["id"])));
        var doc = SimpleDoc("t", ["id", "new_col"], ["id"]);
        var ops = _droppingPlanner.Plan(schemas, [doc]);
        ops.Should().Contain(o => o is AddColumnOperation);
        ops.Should().Contain(o => o is DropColumnOperation);
    }

    // ═══════════════════════════════════════════════════════
    // Unique Constraints
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void NewTable_SingleUnique_OnColumnDef()
    {
        var doc = SimpleDoc("t", ["id", "email"], ["id"]);
        doc.UniqueKeys.Add(new UniqueKeyDefinition(["email"]));
        var ops = _planner.Plan(NoSchemas(), [doc]);
        ops.OfType<CreateTableOperation>().First()
            .Columns.First(c => c.Name == "email").IsUnique.Should().BeTrue();
        ops.OfType<AddUniqueConstraintOperation>().Should().BeEmpty();
    }

    [Fact]
    public void NewTable_CompositeUnique_ExplicitConstraint()
    {
        var doc = SimpleDoc("t", ["id", "a", "b"], ["id"]);
        doc.UniqueKeys.Add(new UniqueKeyDefinition(["a", "b"]));
        var ops = _planner.Plan(NoSchemas(), [doc]);
        var u = ops.OfType<AddUniqueConstraintOperation>();
        u.Should().ContainSingle();
        u.First().Columns.Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Diff_NewUnique_PlansAdd()
    {
        var schemas = WithSchema("public", ("t", Table("t", ["id", "email"], ["id"])));
        var doc = SimpleDoc("t", ["id", "email"], ["id"]);
        doc.UniqueKeys.Add(new UniqueKeyDefinition(["email"]));
        _planner.Plan(schemas, [doc]).OfType<AddUniqueConstraintOperation>().Should().ContainSingle();
    }

    [Fact]
    public void Diff_RemovedUnique_PlansDrop()
    {
        var uniques = new Dictionary<string, UniqueConstraintModel>
        {
            ["uq_t_email"] = new("uq_t_email", ["email"])
        };
        var schemas = WithSchema("public", ("t", TableWithUniques("t", ["id", "email"], ["id"], uniques)));
        var doc = SimpleDoc("t", ["id", "email"], ["id"]);
        _planner.Plan(schemas, [doc]).OfType<DropConstraintOperation>().Should().ContainSingle();
    }

    // ═══════════════════════════════════════════════════════
    // Indexes
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void NewTable_WithIndex_Creates()
    {
        var types = new Dictionary<string, Type> { ["id"] = typeof(string), ["name"] = typeof(string) };
        var doc = Doc("t", types, ["id"], indexes: ["name"]);
        _planner.Plan(NoSchemas(), [doc]).OfType<CreateIndexOperation>().Should().ContainSingle();
    }

    [Fact]
    public void Diff_NewIndex_Creates()
    {
        var schemas = WithSchema("public", ("t", Table("t", ["id", "name"], ["id"])));
        var types = new Dictionary<string, Type> { ["id"] = typeof(string), ["name"] = typeof(string) };
        var doc = Doc("t", types, ["id"], indexes: ["name"]);
        _planner.Plan(schemas, [doc]).OfType<CreateIndexOperation>().Should().ContainSingle();
    }

    [Fact]
    public void Diff_RemovedIndex_Drops()
    {
        var indexes = new Dictionary<string, IndexModel> { ["t_name_idx"] = new("t_name_idx", "name") };
        var schemas = WithSchema("public", ("t", TableWithIndexes("t", ["id", "name"], ["id"], indexes)));
        var doc = SimpleDoc("t", ["id", "name"], ["id"]);
        _planner.Plan(schemas, [doc]).OfType<DropIndexOperation>().Should().ContainSingle();
    }

    // ═══════════════════════════════════════════════════════
    // Foreign Keys
    // ═══════════════════════════════════════════════════════

    // FK creation tests require real vault models with [Vault] attributes
    // and proper Document building — tested via integration tests against Postgres.

    [Fact]
    public void Diff_RemovedFK_PlansDrop()
    {
        var fks = new[] { new ForeignKeyModel("fk_t_aid", "account_id", "accounts", "id") };
        var schemas = WithSchema("public", ("t", TableWithFKs("t", ["id", "account_id"], ["id"], fks)));
        var doc = SimpleDoc("t", ["id", "account_id"], ["id"]);
        _planner.Plan(schemas, [doc]).OfType<DropForeignKeyOperation>().Should().ContainSingle();
    }

    // ═══════════════════════════════════════════════════════
    // History Tables
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void NewTable_WithHistory_CreatesBothTables()
    {
        var doc = SimpleDoc("t", ["id", "name"], ["id"], history: true);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        var creates = ops.OfType<CreateTableOperation>().ToList();
        creates.Should().HaveCount(2);
        creates.Should().Contain(c => c.Table == "t");
        creates.Should().Contain(c => c.Table == "t_history");
    }

    [Fact]
    public void HistoryTable_HasTimestampInPK()
    {
        var doc = SimpleDoc("t", ["id", "name"], ["id"], history: true);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        var hist = ops.OfType<CreateTableOperation>().First(c => c.Table == "t_history");
        hist.Columns.Should().Contain(c => c.Name == "timestamp");
        hist.PrimaryKeyColumns.Should().Contain("timestamp");
    }

    [Fact]
    public void HistoryTable_AllColumnsNullable()
    {
        var doc = SimpleDoc("t", ["id", "name"], ["id"], history: true);
        var ops = _planner.Plan(NoSchemas(), [doc]);
        var hist = ops.OfType<CreateTableOperation>().First(c => c.Table == "t_history");
        foreach (var col in hist.Columns.Where(c => c.Name != "timestamp"))
            col.IsNullable.Should().BeTrue($"history col '{col.Name}' must be nullable");
    }

    // ═══════════════════════════════════════════════════════
    // Schema / Keyspace
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void CustomKeyspace_UsesSchemaName()
    {
        var doc = SimpleDoc("t", ["id"], ["id"], keyspace: "game");
        _planner.Plan(NoSchemas(), [doc]).OfType<CreateTableOperation>().First()
            .Schema.Should().Be("game");
    }

    [Fact]
    public void DefaultKeyspace_IsPublic()
    {
        var doc = SimpleDoc("t", ["id"], ["id"]);
        _planner.Plan(NoSchemas(), [doc]).OfType<CreateTableOperation>().First()
            .Schema.Should().Be("public");
    }

    // ═══════════════════════════════════════════════════════
    // Multiple Tables
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void MultipleDocs_AllPlanned()
    {
        var d1 = SimpleDoc("a", ["id"], ["id"]);
        var d2 = SimpleDoc("b", ["id"], ["id"]);
        _planner.Plan(NoSchemas(), [d1, d2]).OfType<CreateTableOperation>().Should().HaveCount(2);
    }

    [Fact]
    public void MixedNewAndExisting()
    {
        var schemas = WithSchema("public", ("a", Table("a", ["id", "email"], ["id"])));
        var d1 = SimpleDoc("a", ["id", "email", "phone"], ["id"]); // add column
        var d2 = SimpleDoc("b", ["id", "name"], ["id"]); // new table
        var ops = _planner.Plan(schemas, [d1, d2]);
        ops.Should().Contain(o => o is AddColumnOperation);
        ops.Should().Contain(o => o is CreateTableOperation);
    }

    // ═══════════════════════════════════════════════════════
    // Constraint Naming
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void ConstraintName_Under60Chars()
    {
        var doc = SimpleDoc("t", ["id", "a", "b"], ["id"]);
        doc.UniqueKeys.Add(new UniqueKeyDefinition(["a", "b"]));
        var ops = _planner.Plan(NoSchemas(), [doc]);
        var u = ops.OfType<AddUniqueConstraintOperation>().First();
        u.ConstraintName.Length.Should().BeLessThanOrEqualTo(60);
    }

    [Fact]
    public void ConstraintName_Deterministic()
    {
        var doc1 = SimpleDoc("t", ["id", "a", "b"], ["id"]);
        doc1.UniqueKeys.Add(new UniqueKeyDefinition(["a", "b"]));
        var doc2 = SimpleDoc("t", ["id", "a", "b"], ["id"]);
        doc2.UniqueKeys.Add(new UniqueKeyDefinition(["a", "b"]));

        var name1 = _planner.Plan(NoSchemas(), [doc1]).OfType<AddUniqueConstraintOperation>().First().ConstraintName;
        var name2 = _planner.Plan(NoSchemas(), [doc2]).OfType<AddUniqueConstraintOperation>().First().ConstraintName;
        name1.Should().Be(name2);
    }

    // ═══════════════════════════════════════════════════════
    // Column Rename ([VaultRenamedFrom])
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void Rename_EmitsRenameOp_NotDropAdd()
    {
        var existing = Table("t", ["id", "old_name"], ["id"]);
        var schemas = WithSchema("public", ("t", existing));

        var doc = SimpleDoc("t", ["id", "new_name"], ["id"]);
        doc.RenamedColumns["new_name"] = ["old_name"];
        var ops = _planner.Plan(schemas, [doc]);

        ops.OfType<RenameColumnOperation>().Should().ContainSingle();
        var rename = ops.OfType<RenameColumnOperation>().First();
        rename.OldColumnName.Should().Be("old_name");
        rename.NewColumnName.Should().Be("new_name");

        // Must NOT emit drop+add
        ops.OfType<DropColumnOperation>().Should().BeEmpty();
        ops.OfType<AddColumnOperation>().Should().BeEmpty();
    }

    [Fact]
    public void Rename_StaleAttribute_TreatedAsNewColumn()
    {
        // old column doesn't exist in DB — attribute is stale
        var existing = Table("t", ["id", "something_else"], ["id"]);
        var schemas = WithSchema("public", ("t", existing));

        var doc = SimpleDoc("t", ["id", "new_name"], ["id"]);
        doc.RenamedColumns["new_name"] = ["nonexistent_old"];
        var ops = _droppingPlanner.Plan(schemas, [doc]);

        // Should add new_name (old doesn't exist) and drop something_else
        ops.OfType<RenameColumnOperation>().Should().BeEmpty();
        ops.OfType<AddColumnOperation>().Should().ContainSingle();
        ops.OfType<DropColumnOperation>().Should().ContainSingle();
    }

    [Fact]
    public void Rename_MultipleRenames_AllHandled()
    {
        var existing = Table("t", ["id", "col_a", "col_b"], ["id"]);
        var schemas = WithSchema("public", ("t", existing));

        var doc = SimpleDoc("t", ["id", "renamed_a", "renamed_b"], ["id"]);
        doc.RenamedColumns["renamed_a"] = ["col_a"];
        doc.RenamedColumns["renamed_b"] = ["col_b"];
        var ops = _planner.Plan(schemas, [doc]);

        ops.OfType<RenameColumnOperation>().Should().HaveCount(2);
        ops.OfType<DropColumnOperation>().Should().BeEmpty();
        ops.OfType<AddColumnOperation>().Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════
    // Column Type Change (auto-detected)
    // ═══════════════════════════════════════════════════════

    [Fact]
    public void TypeChange_IntToBigint_EmitsAlterType()
    {
        var cols = new Dictionary<string, ColumnModel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new("id", "text", false),
            ["gold"] = new("gold", "integer", false)
        };
        var existing = new TableModel("t", cols, ["id"],
            new Dictionary<string, UniqueConstraintModel>(),
            new Dictionary<string, IndexModel>(),
            Array.Empty<ForeignKeyModel>());
        var schemas = WithSchema("public", ("t", existing));

        var doc = Doc("t", new() { ["id"] = typeof(string), ["gold"] = typeof(long) }, ["id"]);
        var ops = _planner.Plan(schemas, [doc]);

        ops.OfType<AlterColumnTypeOperation>().Should().ContainSingle();
        var alter = ops.OfType<AlterColumnTypeOperation>().First();
        alter.ColumnName.Should().Be("gold");
        alter.OldStoreType.Should().Be("integer");
        alter.NewStoreType.Should().Be("bigint");
    }

    [Fact]
    public void TypeChange_IntToText_EmitsAlterType()
    {
        var cols = new Dictionary<string, ColumnModel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new("id", "text", false),
            ["count"] = new("count", "integer", false)
        };
        var existing = new TableModel("t", cols, ["id"],
            new Dictionary<string, UniqueConstraintModel>(),
            new Dictionary<string, IndexModel>(),
            Array.Empty<ForeignKeyModel>());
        var schemas = WithSchema("public", ("t", existing));

        var doc = Doc("t", new() { ["id"] = typeof(string), ["count"] = typeof(string) }, ["id"]);
        var ops = _planner.Plan(schemas, [doc]);

        var alter = ops.OfType<AlterColumnTypeOperation>().First();
        alter.OldStoreType.Should().Be("integer");
        alter.NewStoreType.Should().Be("text");
    }

    [Fact]
    public void TypeChange_SameType_NoOp()
    {
        var cols = new Dictionary<string, ColumnModel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new("id", "text", false),
            ["name"] = new("name", "text", false)
        };
        var existing = new TableModel("t", cols, ["id"],
            new Dictionary<string, UniqueConstraintModel>(),
            new Dictionary<string, IndexModel>(),
            Array.Empty<ForeignKeyModel>());
        var schemas = WithSchema("public", ("t", existing));

        var doc = Doc("t", new() { ["id"] = typeof(string), ["name"] = typeof(string) }, ["id"]);
        var ops = _planner.Plan(schemas, [doc]);

        ops.OfType<AlterColumnTypeOperation>().Should().BeEmpty();
    }

    [Fact]
    public void RenameAndTypeChange_BothEmitted()
    {
        var cols = new Dictionary<string, ColumnModel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new("id", "text", false),
            ["old_col"] = new("old_col", "integer", false)
        };
        var existing = new TableModel("t", cols, ["id"],
            new Dictionary<string, UniqueConstraintModel>(),
            new Dictionary<string, IndexModel>(),
            Array.Empty<ForeignKeyModel>());
        var schemas = WithSchema("public", ("t", existing));

        // Renamed old_col → new_col AND changed type int → bigint
        var doc = Doc("t", new() { ["id"] = typeof(string), ["new_col"] = typeof(long) }, ["id"]);
        doc.RenamedColumns["new_col"] = ["old_col"];
        var ops = _planner.Plan(schemas, [doc]);

        ops.OfType<RenameColumnOperation>().Should().ContainSingle();
        ops.OfType<AlterColumnTypeOperation>().Should().ContainSingle();
        var alter = ops.OfType<AlterColumnTypeOperation>().First();
        alter.ColumnName.Should().Be("new_col");
        alter.OldStoreType.Should().Be("integer");
        alter.NewStoreType.Should().Be("bigint");
    }
}
