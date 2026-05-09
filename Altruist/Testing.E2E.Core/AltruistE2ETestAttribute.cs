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
/// per-class DI scope like <see cref="AltruistTestAttribute"/>.
///
/// <para><b>Setup contract.</b> Tests are expected to take an
/// <see cref="AltruistE2EFixture"/> via xUnit's
/// <c>IClassFixture&lt;AltruistE2EFixture&gt;</c>; the fixture loads
/// <c>config.yml</c> + <c>config-test.yml</c>, boots the client DI container,
/// and exposes <c>TestHttpClient</c>/<c>TestTcpClient</c> for the test to drive.</para>
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
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "E2E");
    }
}
