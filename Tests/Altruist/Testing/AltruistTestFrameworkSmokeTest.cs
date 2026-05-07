/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Testing;

using Microsoft.Extensions.Configuration;

using Moq;

[assembly: Xunit.TestFramework("Altruist.Testing.AltruistTestFramework", "Altruist.Testing")]

namespace AltruistTests.Testing;

/// <summary>
/// Test fixture services used only by <see cref="AltruistTestFrameworkSmokeTest"/>.
/// They are real <c>[Service]</c>-registered classes — exactly what production code looks like —
/// so the test exercises the actual DI graph the framework builds.
/// </summary>
public interface ITransitiveDep
{
    string Greet(string who);
}

[Service(typeof(ITransitiveDep))]
public sealed class RealTransitiveDep : ITransitiveDep
{
    public string Greet(string who) => $"hello {who}";
}

public interface IAltruistTestSubject
{
    string GreetVia(string who);
}

[Service(typeof(IAltruistTestSubject))]
public sealed class RealTestSubject : IAltruistTestSubject
{
    private readonly ITransitiveDep _dep;
    public RealTestSubject(ITransitiveDep dep) => _dep = dep;
    public string GreetVia(string who) => _dep.Greet(who);
}

/// <summary>
/// Smoke test: <see cref="AltruistTestAttribute"/> resolves the test class from the real DI container,
/// and <see cref="MockAttribute"/> substitutes a Moq mock that's visible to other DI-resolved services.
/// </summary>
[AltruistTest(RunModuleLoaders = false)]
public class AltruistTestFrameworkSmokeTest
{
    private readonly IConfiguration _config;
    private readonly IAltruistTestSubject _subject;
    private readonly ITransitiveDep _depMock;

    public AltruistTestFrameworkSmokeTest(
        IConfiguration config,
        IAltruistTestSubject subject,
        [Mock] ITransitiveDep depMock)
    {
        _config = config;
        _subject = subject;
        _depMock = depMock;
    }

    [Fact]
    public void RealService_Is_Resolved_From_DI()
    {
        Assert.NotNull(_config);
    }

    [Fact]
    public void Mock_Param_Is_A_Moq_Mock()
    {
        Assert.NotNull(Mock.Get(_depMock));
    }

    [Fact]
    public void Mock_Override_Is_Visible_To_Sibling_Real_Service()
    {
        // RealTestSubject was constructed by DI from the per-class container,
        // where ITransitiveDep was replaced with our Moq mock. Stubbing the mock
        // here proves the subject's _dep field IS the same mock instance.
        Mock.Get(_depMock).Setup(d => d.Greet("world")).Returns("mocked!");
        Assert.Equal("mocked!", _subject.GreetVia("world"));
    }
}

/// <summary>
/// Sanity check that classes WITHOUT <see cref="AltruistTestAttribute"/> still run via default xUnit
/// behavior — no DI involvement, no breakage.
/// </summary>
public class NonAltruistTest
{
    [Fact]
    public void Plain_Xunit_Test_Still_Works() => Assert.True(true);
}
