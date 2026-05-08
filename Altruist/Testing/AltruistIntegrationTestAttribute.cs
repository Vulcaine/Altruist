/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Testing;

/// <summary>
/// Marker on a test class that needs a real Altruist server (HTTP + transports)
/// running for the duration of its <c>[Fact]</c>s. The framework boots a single
/// process-shared server lazily on the first <c>[AltruistIntegrationTest]</c>
/// class encountered, then keeps it alive until the test process exits.
///
/// <para><b>When to use this vs <see cref="AltruistTestAttribute"/></b>:
/// reach for <c>[AltruistIntegrationTest]</c> only when the test exercises the
/// network surface itself (TCP packet round-trip, HTTP signup/login, broadcast
/// routing, multi-client visibility). For service-level assertions
/// (vault writes, business logic), <c>[AltruistTest]</c> is faster and gives
/// per-test isolation.</para>
///
/// <para><b>Config.</b> Boot uses <c>config.yml</c> + an optional
/// <c>config-test.yml</c> overlay loaded from the test assembly's base
/// directory. Override host/port/database/etc. there.</para>
///
/// <para><b>Database.</b> The configured persistence provider is used as-is —
/// no per-class schema isolation in this mode (the live server's DI is bound to
/// one schema for its whole lifetime). Tests must use unique IDs (Guid) to
/// avoid colliding with parallel tests in the same assembly.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AltruistIntegrationTestAttribute : Attribute
{
}
