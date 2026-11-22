/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Xunit.Abstractions;
using Xunit.Sdk;

namespace Altruist.Testing.E2E;

/// <summary>
/// Marks a test class as a client-side end-to-end test. Tests so marked drive
/// the same Altruist client transport stack the Unity client uses (codec,
/// dispatcher, mirror) against a real running server, instead of building a
/// per-class DI scope like <c>[AltruistTest]</c> (Altruist.Testing).
///
/// <para><b>When to use.</b> <c>[AltruistTest]</c> for service-level tests against the DI
/// container (fast, per-class schema isolation); <c>[AltruistIntegrationTest]</c> to exercise the
/// network surface against a server the test process boots itself; <c>[AltruistE2ETest]</c> to
/// drive the client stack against an externally running server (docker stack, staging), from
/// <c>dotnet test</c> or, through <see cref="AltruistTestRunner"/>, from hosts without xUnit
/// (a game-engine editor, a console app).</para>
///
/// <para><b>Setup contract.</b> Tests are expected to take an
/// <c>AltruistE2EFixture</c> via xUnit's
/// <c>IClassFixture&lt;AltruistE2EFixture&gt;</c> (Altruist.Testing); the fixture loads
/// <c>config.yml</c> + an optional <c>config.E2E.yml</c> overlay and builds per-test client stacks
/// (<c>TestPlayerSession</c> with its <c>TestHttpClient</c>/<c>TestTcpClient</c>, a packet
/// dispatcher, an inventory mirror) for the test to drive.</para>
///
/// <para><b>Server requirement.</b> A real server must be running and configured
/// with <c>altruist:e2e:enabled=true</c>. The standard pattern is a dedicated
/// <c>docker-compose-e2e.yml</c> stack with a separate Postgres database, so
/// tests never touch production data.</para>
///
/// <para>This attribute also adds a <c>Category=E2E</c> xUnit trait so test runs
/// can opt-in or opt-out via <c>dotnet test --filter Category=E2E</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
[TraitDiscoverer("Altruist.Testing.E2E.E2ETraitDiscoverer", "Altruist.Testing")]
public sealed class AltruistE2ETestAttribute : Attribute, ITraitAttribute
{
}

/// <summary>
/// xUnit trait discoverer for <see cref="AltruistE2ETestAttribute"/>. Emits a
/// single <c>Category=E2E</c> trait so test runs can be filtered with
/// <c>dotnet test --filter Category=E2E</c> (or <c>!=E2E</c> to exclude them
/// from a fast unit-test run).
/// </summary>
public sealed class E2ETraitDiscoverer : ITraitDiscoverer
{
    /// <summary>Returns the single trait <c>Category=E2E</c>.</summary>
    /// <param name="traitAttribute">The attribute instance (unused).</param>
    /// <returns>The traits.</returns>
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "E2E");
    }
}
