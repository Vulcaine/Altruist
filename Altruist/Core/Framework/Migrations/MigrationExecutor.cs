namespace Altruist.Migrations;

public interface IMigrationExecutor
{
    Task ApplyAsync(IKeyspace schema, IReadOnlyList<MigrationOperation> operations);
}

