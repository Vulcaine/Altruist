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

namespace Altruist.Contracts;

/// <summary>Marker for configuration types that set up a cache backend; pairs with <see cref="ICacheServiceToken"/>.</summary>
public interface ICacheConfiguration : IAltruistConfiguration
{

}

/// <summary>Marker for configuration types that set up a transport; pairs with <see cref="ITransportServiceToken"/>.</summary>
public interface ITransportConfiguration : IAltruistConfiguration
{

}

/// <summary>Configuration for a database backend; pairs with <see cref="IDatabaseServiceToken"/>.</summary>
public interface IDatabaseConfiguration : IAltruistConfiguration
{
    /// <summary>Name of the database (or keyspace) to use.</summary>
    string DatabaseName { get; }
}

/// <summary>
/// Identifies one backend implementation (a specific transport, cache or database provider) and its configuration type.
/// </summary>
/// <remarks>Tokens are typically singletons exposed by the backend package and registered in DI; the server context
/// collects them to describe and validate the active setup (see <see cref="IAltruistContext"/>).</remarks>
/// <typeparam name="TConfiguration">The configuration kind this backend belongs to.</typeparam>
public interface IServiceToken<TConfiguration> where TConfiguration : IAltruistConfiguration
{
    /// <summary>One-line description printed in the startup banner.</summary>
    public string Description { get; }
}

/// <summary>Token identifying the active cache backend (e.g. in-memory or Redis).</summary>
public interface ICacheServiceToken : IServiceToken<ICacheConfiguration>
{

}

/// <summary>Token identifying the active transport backend.</summary>
public interface ITransportServiceToken : IServiceToken<ITransportConfiguration>
{

}

/// <summary>Token identifying a database backend; several may be active at once.</summary>
public interface IDatabaseServiceToken : IServiceToken<IDatabaseConfiguration>
{

}

/// <summary>Self-typed builder contract for backend setups that are finalized against the server context.</summary>
/// <remarks>Legacy builder API; currently not used by the built-in backends, which are configured from YAML.</remarks>
/// <typeparam name="TSelf">The concrete setup type (for fluent chaining).</typeparam>
public interface ISetup<TSelf> where TSelf : ISetup<TSelf>
{
    /// <summary>Finalizes the setup.</summary>
    /// <param name="settings">The server context to register into.</param>
    Task Build(IAltruistContext settings);
}

/// <summary><see cref="ISetup{TSelf}"/> for backends reached through one or more contact points (cluster nodes).</summary>
/// <typeparam name="TSelf">The concrete setup type (for fluent chaining).</typeparam>
public interface IContactSetup<TSelf> : ISetup<TSelf> where TSelf : IContactSetup<TSelf>
{
    /// <summary>Adds a node by host and port.</summary>
    /// <param name="host">Host name or address.</param>
    /// <param name="port">Port.</param>
    /// <returns>This setup, for chaining.</returns>
    TSelf AddContactPoint(string host, int port);
    /// <summary>Adds a node from a connection string.</summary>
    /// <param name="connectionString">Backend-specific connection string.</param>
    /// <returns>This setup, for chaining.</returns>
    TSelf AddContactPoint(string connectionString);
}
