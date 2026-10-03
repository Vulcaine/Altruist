/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.UORM;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>General purpose vault for the query / literal / ordering tests.</summary>
[Vault("it_records", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItRecord : VaultModel
{
    [VaultColumn("name")]
    [VaultColumnIndex]
    public string Name { get; set; } = "";

    [VaultColumn("rank")] public int Rank { get; set; }

    [VaultColumn("ratio")] public double Ratio { get; set; }

    [VaultColumn("happened-at")] public DateTime HappenedAt { get; set; }

    [VaultColumn("tag", nullable: true)] public string? Tag { get; set; }
}

/// <summary>First release of a table with a single unique key.</summary>
[Vault("it_unique_probe", Keyspace: PostgresDatabaseFixture.Schema)]
[VaultUniqueKey(nameof(Name))]
public sealed class ItUniqueProbeV1 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
}

/// <summary>The same table one release later: a new nullable unique column on an existing table.</summary>
[Vault("it_unique_probe", Keyspace: PostgresDatabaseFixture.Schema)]
[VaultUniqueKey(nameof(Name))]
[VaultUniqueKey(nameof(Email))]
public sealed class ItUniqueProbeV2 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("email", nullable: true)] public string? Email { get; set; }
}

/// <summary>A table with an indexed column (exercises CREATE INDEX generation).</summary>
[Vault("it_indexed", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItIndexed : VaultModel
{
    [VaultColumn("owner")]
    [VaultColumnIndex]
    public string Owner { get; set; } = "";
}

/// <summary>Row shape for single-value queries through the provider.</summary>
public sealed class ItTextRow
{
    public string Value { get; set; } = "";
}
