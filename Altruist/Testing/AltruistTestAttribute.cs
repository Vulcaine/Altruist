/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Testing;

/// <summary>
/// Marks a test class for resolution from the real Altruist DI container.
/// Constructor parameters resolve from DI; parameters marked <see cref="MockAttribute"/>
/// are substituted with Moq mocks visible to the rest of the per-class container.
///
/// Requires the assembly to register the framework:
/// <c>[assembly: Xunit.TestFramework("Altruist.Testing.AltruistTestFramework", "Altruist.Testing")]</c>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AltruistTestAttribute : Attribute
{
    /// <summary>
    /// Run <c>[AltruistModuleLoader]</c> static methods after DI is built.
    /// Defaults to true. Set false for pure-formula tests that need no item/mob registries
    /// — saves ~1–2s per assembly run.
    /// </summary>
    public bool RunModuleLoaders { get; set; } = true;

    /// <summary>
    /// Keep the per-class test schema after the test run completes. Useful for
    /// post-mortem inspection of inserted rows when a test fails. Defaults to false
    /// (the schema is dropped at the end of the class). The schema is always
    /// purged + recreated at the start of each run regardless of this flag, so
    /// keeping it does not pollute the next run.
    /// </summary>
    public bool KeepSchema { get; set; }
}
