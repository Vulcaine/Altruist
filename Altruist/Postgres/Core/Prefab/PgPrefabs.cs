using Altruist.Persistence.Postgres;

namespace Altruist.Persistence;

[Service(typeof(IPrefabs))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PgPrefabs : IPrefabs
{
    private readonly ISqlDatabaseProvider _db;
    private readonly IPgModelSqlMetadataProvider _sqlMeta;

    public PgPrefabs(ISqlDatabaseProvider db, IPgModelSqlMetadataProvider sqlMeta)
    {
        _db = db;
        _sqlMeta = sqlMeta;
    }

    public IPrefabQuery<TPrefab> Query<TPrefab>()
        where TPrefab : PrefabModel, new()
        => new PgPrefabQuery<TPrefab>(_db, _sqlMeta);
}
