
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

namespace Altruist.Migrations
{
    /// <summary>
    /// Provider-agnostic contract for applying vault schema to a physical database.
    /// Implementations are provider-specific (Postgres, Scylla, etc.).
    /// </summary>
    public interface IVaultSchemaMigrator
    {
        /// <summary>
        /// Ensure that all tables / indexes / constraints for the given vault models
        /// exist in the given logical schema (keyspace).
        /// </summary>
        Task Migrate(IKeyspace schema, Type[] modelTypes, CancellationToken cancellationToken = default);
    }
}
