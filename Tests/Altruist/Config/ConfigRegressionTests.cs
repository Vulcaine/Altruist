/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Altruist.Config;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppConfigLoaderCollection
{
    public const string Name = "app-config-loader";
}

/// <summary>
/// The environment-variable provider strips the <c>ALTRUIST__</c> prefix, so
/// <c>ALTRUIST__SECURITY__KEY</c> became <c>security:key</c> and never overrode
/// <c>altruist:security:key</c>. Process-global state (env vars, the cached configuration) is
/// saved and restored around each test.
/// </summary>
[Collection(AppConfigLoaderCollection.Name)]
public sealed class AppConfigLoaderRegressionTests
{
    private static readonly FieldInfo CachedConfig =
        typeof(AppConfigLoader).GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static IConfiguration LoadFresh(params (string Key, string Value)[] env)
    {
        var previous = (IConfiguration?)CachedConfig.GetValue(null);
        try
        {
            foreach (var (k, v) in env)
                Environment.SetEnvironmentVariable(k, v);
            AppConfigLoader.Reset();
            var cfg = AppConfigLoader.Load(Array.Empty<string>());
            // Materialize the values we need before the env vars are removed.
            return new ConfigurationBuilder().AddInMemoryCollection(cfg.AsEnumerable()).Build();
        }
        finally
        {
            foreach (var (k, _) in env)
                Environment.SetEnvironmentVariable(k, null);
            if (previous is null) AppConfigLoader.Reset();
            else AppConfigLoader.Set(previous);
        }
    }

    [Fact]
    public void Prefixed_environment_variables_map_under_the_altruist_root()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var cfg = LoadFresh(($"ALTRUIST__TESTCFG{suffix}__SECURITY__KEY", "from-env"));

        Assert.Equal("from-env", cfg[$"altruist:testcfg{suffix}:security:key"]);
        // The stripped form is still there for backwards compatibility.
        Assert.Equal("from-env", cfg[$"testcfg{suffix}:security:key"]);
    }

    [Fact]
    public void Environment_overrides_values_with_the_same_rooted_key()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var cfg = LoadFresh(
            ($"ALTRUIST__TESTCFG{suffix}__SERVER__PORT", "9999"),
            ($"ALTRUIST__TESTCFG{suffix}__A__B__C", "deep"));

        Assert.Equal("9999", cfg[$"altruist:testcfg{suffix}:server:port"]);
        Assert.Equal("deep", cfg[$"altruist:testcfg{suffix}:a:b:c"]);
    }
}

/// <summary>
/// [ConfigConverter(typeof(X))] registered the converter as service type X (e.g. List&lt;string&gt;):
/// an instance behind a service type it does not implement, which fails ServiceProvider validation.
/// </summary>
public sealed class ConfigConverterAttributeRegressionTests
{
    [ConfigConverter(typeof(List<string>))]
    public sealed class CsvListConverter : IConfigConverter<List<string>>
    {
        public Type TargetType => typeof(List<string>);
        public List<string>? Convert(string value) => value.Split(',').ToList();
        object? IConfigConverter.Convert(string value) => Convert(value);
    }

    [Fact]
    public void Converter_is_registered_as_itself_not_as_its_target_type()
    {
        var attr = typeof(CsvListConverter).GetCustomAttribute<ConfigConverterAttribute>()!;
        Assert.Equal(typeof(List<string>), attr.TargetType);
        Assert.Null(attr.ServiceType); // null => the implementation type itself
        Assert.Equal(ServiceLifetime.Singleton, attr.Lifetime);
    }

    [Fact]
    public void Registering_by_the_attribute_passes_service_provider_validation()
    {
        var attr = typeof(CsvListConverter).GetCustomAttribute<ConfigConverterAttribute>()!;
        var services = new ServiceCollection();
        ((IServiceCollection)services).Add(new ServiceDescriptor(attr.ServiceType ?? typeof(CsvListConverter), typeof(CsvListConverter), attr.Lifetime));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(provider.GetService<CsvListConverter>());
    }

    [Fact]
    public void Null_target_type_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigConverterAttribute(null!));
    }
}
