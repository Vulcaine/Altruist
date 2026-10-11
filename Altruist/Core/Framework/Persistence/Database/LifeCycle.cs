/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

namespace Altruist.Persistence;

/// <summary>
/// Seeds or prepares data after the database schema has been migrated at startup. Implement it in any loaded
/// assembly; it is discovered automatically (no registration needed).
/// </summary>
/// <remarks>
/// <para>
/// Each implementation is created with <c>ActivatorUtilities</c> (constructor injection works) and run once per
/// <b>server startup</b>, after schema creation and migration, while the database bootstrap lock is held (so only
/// one server of a fleet runs initializers at a time). Initializers therefore run again on every restart and must be
/// idempotent: check before inserting, or save with deterministic ids (<see cref="IIdGenerator"/>).
/// </para>
/// <para>
/// A thrown exception is logged and the next initializer still runs; startup is not aborted.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class DefaultItemsSeeder(IVault&lt;ItemDefinition&gt; items) : IDatabaseInitializer
/// {
///     public int Order =&gt; 10;
///     public async Task InitializeAsync(IServiceProvider services)
///     {
///         if (await items.CountAsync() &gt; 0) return;
///         await items.SaveBatchAsync(DefaultItems.All);
///     }
/// }
/// </code>
/// </example>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Lower numbers run first. Higher numbers run later. Ties run in order of full type name.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Performs the initialization (typically seeding vault rows).
    /// </summary>
    /// <param name="services">The application service provider, for resolving vaults and other services.</param>
    Task InitializeAsync(IServiceProvider services);
}

