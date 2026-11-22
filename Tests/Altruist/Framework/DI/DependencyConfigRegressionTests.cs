/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;
using System.Runtime.Loader;

using Altruist;
using Altruist.Dashboard;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Framework.DI;

public sealed class ConvertedColor
{
    public string Name { get; set; } = "";
}

[ConfigConverter(typeof(ConvertedColor))]
public sealed class ConvertedColorConverter : IConfigConverter<ConvertedColor>
{
    public Type TargetType => typeof(ConvertedColor);
    public ConvertedColor Convert(string value) => new() { Name = "converted:" + value };
    object? IConfigConverter.Convert(string value) => Convert(value);
}

/// <summary>DI and configuration regressions #96, #97, #99, #101, #102, #104, #105.</summary>
public sealed class DependencyConfigRegressionTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void A_live_value_uses_the_attribute_default_while_the_key_is_missing()
    {
        var live = (ILiveConfigValue<float>)DependencyResolver.ResolveFromConfig(Config(new()), typeof(ILiveConfigValue<float>),
            new AppConfigValueAttribute("test:live:missing-step", "0.25"), NullLogger.Instance)!;

        Assert.Equal(0.25f, live.Current);
    }

    [Fact]
    public void Values_set_on_the_registered_mutable_provider_reach_configurations_built_from_its_source()
    {
        var provider = new MutableConfigProvider();
        var cfg = new ConfigurationBuilder().Add(new MutableConfigSource(provider)).Build();

        provider.Set("test:mutable:key", "on");

        Assert.Equal("on", cfg["test:mutable:key"]);
    }

    [Fact]
    public void ServiceKey_can_only_be_put_on_parameters()
    {
        var usage = typeof(global::Altruist.ServiceKeyAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

        Assert.Equal(AttributeTargets.Parameter, usage.ValidOn);
    }

    [Fact]
    public void A_converter_also_converts_a_scalar_value_of_its_complex_type()
    {
        var cfg = Config(new() { ["test:theme:color"] = "red" });
        DependencyResolver.EnsureConverters(new ServiceCollection(), cfg, NullLogger.Instance);

        var color = (ConvertedColor)DependencyResolver.ResolveFromConfig(cfg, typeof(ConvertedColor),
            new AppConfigValueAttribute("test:theme:color"), NullLogger.Instance)!;

        Assert.Equal("converted:red", color.Name);
    }

    [Fact]
    public void Inject_before_the_root_provider_exists_fails_instead_of_building_a_throwaway_container()
    {
        // A fresh copy of the DI assembly: its static root provider is unset, as during bootstrap.
        var context = new AssemblyLoadContext("inject-before-root", isCollectible: true);
        try
        {
            var asm = context.LoadFromAssemblyPath(typeof(Dependencies).Assembly.Location);
            var inject = asm.GetType(typeof(Dependencies).FullName!)!.GetMethod(nameof(Dependencies.Inject), new[] { typeof(Type) })!;

            var error = Assert.Throws<TargetInvocationException>(() => inject.Invoke(null, new object[] { typeof(IServiceProvider) }));

            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Contains("before the root service provider was built", error.InnerException!.Message);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void Option_snapshots_bind_the_keys_the_framework_reads()
    {
        var cfg = Config(new()
        {
            ["altruist:server:http:host"] = "0.0.0.0",
            ["altruist:server:http:port"] = "8091",
            ["altruist:server:transport:mode"] = "tcp",
            ["altruist:game:engine:frequency"] = "20",
            ["altruist:game:worlds:items:0:id"] = "w1",
            ["altruist:game:worlds:items:0:data-path"] = "worlds/w1.json",
        });
        var services = new ServiceCollection();

        AltruistDI.BindConfigurationClasses(services, cfg, NullLogger.Instance);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<AltruistConfigOptions>();
        Assert.Equal("0.0.0.0", options.Server.Http.Host);
        Assert.Equal(8091, options.Server.Http.Port);
        Assert.Equal("tcp", options.Server.Transport.Mode);
        Assert.Equal(20, options.Game.Engine.EffectiveFramerateHz);
        Assert.Equal("worlds/w1.json", Assert.Single(options.Game.Worlds.Items).DataPath);
    }

    [Fact]
    public void The_engine_framerate_defaults_to_the_engine_default()
    {
        Assert.Equal(30, new EngineConfigOptions().EffectiveFramerateHz);
    }

    [Fact]
    public void The_dashboard_summary_reports_the_configured_engine_rate()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Config(new()));
        services.AddSingleton(new System.Text.Json.JsonSerializerOptions());
        services.AddSingleton(new EngineConfigOptions { Frequency = 20 });
        using var provider = services.BuildServiceProvider();

        var controller = ActivatorUtilities.CreateInstance<AltruistSummaryDashboardController>(provider);
        var summary = Assert.IsType<AltruistSummaryDashboardController.AltruistSummaryDto>(
            Assert.IsType<OkObjectResult>(controller.GetSummary().Result).Value);

        Assert.Equal(20, summary.Engine!.FramerateHz);
    }

    [Fact]
    public void Command_line_arguments_given_after_the_configuration_was_loaded_are_not_silently_ignored()
    {
        AppConfigLoader.Load();

        Assert.Throws<InvalidOperationException>(() =>
            AppConfigLoader.Load(new[] { "--test:args:never-applied=" + Guid.NewGuid().ToString("N") }));
    }
}
