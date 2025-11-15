namespace Altruist.Migrations;

public interface ISchemaInspector
{
    Task<DatabaseModel> GetCurrentModelAsync(IKeyspace schema, CancellationToken ct = default);
}
