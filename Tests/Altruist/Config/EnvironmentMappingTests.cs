/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections;

using Altruist;

using Microsoft.Extensions.Configuration;

namespace Tests.Altruist.Config;

/// <summary>
/// Environment variables map onto every YAML root (not only <c>altruist</c>), with <c>_</c> for
/// <c>-</c>; the environment name falls back to ASPNETCORE_ENVIRONMENT.
/// </summary>
[Collection(AppConfigLoaderCollection.Name)]
public sealed class EnvironmentMappingTests
{
    private static IConfiguration Yaml(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static Dictionary<string, string?> Map(IConfiguration yaml, params (string Key, string Value)[] env) =>
        AppConfigLoader.MapEnvironment(yaml, new Hashtable(env.ToDictionary(e => (object)e.Key, e => (object?)e.Value)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Variables_of_any_yaml_root_map_with_dashes()
    {
        var yaml = Yaml(("myapp:public-base-url", "http://localhost"), ("myapp:email:provider", "log"));
        var map = Map(yaml,
            ("MYAPP__PUBLIC_BASE_URL", "https://example.com"),
            ("MYAPP__EMAIL__RESEND_API_KEY", "secret"),
            ("OTHER__KEY", "ignored"),
            ("PATH", "/usr/bin"));

        Assert.Equal("https://example.com", map["myapp:public-base-url"]);
        // Keys that are not in the YAML (secrets) map too.
        Assert.Equal("secret", map["myapp:email:resend-api-key"]);
        Assert.False(map.ContainsKey("other:key"));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Underscore_keys_present_in_the_yaml_keep_their_underscore()
    {
        var yaml = Yaml(("myapp:snake_case", "x"), ("myapp:kebab-case", "y"));
        var map = Map(yaml, ("MYAPP__SNAKE_CASE", "1"), ("MYAPP__KEBAB_CASE", "2"));
        Assert.Equal("1", map["myapp:snake_case"]);
        Assert.Equal("2", map["myapp:kebab-case"]);
    }

    [Fact]
    public void Altruist_variables_keep_their_verbatim_path_and_gain_the_dashed_one()
    {
        var map = Map(Yaml(), ("ALTRUIST__PERSISTENCE__DATABASE__MAX_POOL_SIZE", "80"), ("ALTRUIST__SECURITY__KEY", "k"));
        Assert.Equal("80", map["altruist:persistence:database:max-pool-size"]);
        Assert.Equal("80", map["altruist:persistence:database:max_pool_size"]);
        Assert.Equal("k", map["altruist:security:key"]);
    }

    [Fact]
    public void Extra_roots_come_from_env_roots()
    {
        var yaml = Yaml(("altruist:config:env-roots:0", "secrets"), ("altruist:config:env-roots:1", "my-app"));
        var map = Map(yaml, ("SECRETS__API_KEY", "a"), ("MY_APP__SOME_VALUE", "b"));
        Assert.Equal("a", map["secrets:api-key"]);
        Assert.Equal("b", map["my-app:some-value"]);
    }

    [Fact]
    public void Environment_variable_names_follow_the_path()
    {
        Assert.Equal("MYAPP__EMAIL__RESEND_API_KEY", AppConfigLoader.EnvironmentVariableName("myapp:email:resend-api-key"));
        Assert.Equal("ALTRUIST__SECURITY__KEY", AppConfigLoader.EnvironmentVariableName("altruist:security:key"));
    }

    [Theory]
    [InlineData(null, null, "Production")]
    [InlineData("Development", null, "Development")]
    [InlineData(null, "Development", "Development")]
    [InlineData("Staging", "Development", "Staging")]
    [InlineData("  ", "Development", "Development")]
    public void Environment_name_prefers_dotnet_then_aspnetcore(string? dotnet, string? aspnetcore, string expected)
    {
        var (d, a) = (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnet);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspnetcore);
            Assert.Equal(expected, AltruistEnvironment.Name);
            Assert.Equal(expected == "Production", AltruistEnvironment.IsProduction);
            Assert.Equal(expected == "Development", AltruistEnvironment.IsDevelopment);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", d);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", a);
        }
    }

    [Theory]
    [InlineData("${SECRET}", "s3cret")]
    [InlineData("${MISSING}", "")]
    [InlineData("${MISSING:-fallback}", "fallback")]
    [InlineData("${EMPTY:-fallback}", "fallback")]
    [InlineData("${SECRET:-fallback}", "s3cret")]
    [InlineData("Game <${MISSING:-no-reply@example.com}>", "Game <no-reply@example.com>")]
    [InlineData("$${SECRET}", "${SECRET}")]
    [InlineData("${not valid}", "${not valid}")]
    [InlineData("plain", "plain")]
    public void Placeholders_expand_environment_variables(string value, string expected) =>
        Assert.Equal(expected, AppConfigLoader.ExpandPlaceholders(value, new Hashtable { ["SECRET"] = "s3cret", ["EMPTY"] = "" }));

    [Fact]
    public void Only_values_with_placeholders_are_expanded_and_environment_overrides_still_win()
    {
        var yaml = Yaml(("altruist:security:captcha:secret-key", "${GAME__TURNSTILE__SECRET_KEY}"),
            ("altruist:email:from", "${GAME__EMAIL__FROM:-Game <no-reply@example.com>}"), ("altruist:email:provider", "log"));
        var env = new Hashtable { ["GAME__TURNSTILE__SECRET_KEY"] = "from-env", ["ALTRUIST__EMAIL__FROM"] = "Override <o@example.com>" };
        var expanded = AppConfigLoader.ExpandPlaceholders(yaml, env).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(2, expanded.Count);
        Assert.Equal("from-env", expanded["altruist:security:captcha:secret-key"]);
        Assert.Equal("Game <no-reply@example.com>", expanded["altruist:email:from"]);

        // The loader's order: YAML, expanded placeholders, then the environment mapping.
        var cfg = new ConfigurationBuilder()
            .AddConfiguration(yaml)
            .AddInMemoryCollection(expanded)
            .AddInMemoryCollection(AppConfigLoader.MapEnvironment(yaml, env))
            .Build();
        Assert.Equal("from-env", cfg["altruist:security:captcha:secret-key"]);
        Assert.Equal("Override <o@example.com>", cfg["altruist:email:from"]);
        Assert.Equal("log", cfg["altruist:email:provider"]);
    }
}
