// Altruist/ConfigLoader.cs
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;

namespace Altruist
{
    /// <summary>
    /// Loads <c>config.yml</c>, <c>config.{environment}.yml</c> (<see cref="AltruistEnvironment.Name"/>),
    /// environment variables and the command line, in that order (later wins).
    /// <para>
    /// Environment variables override YAML keys under every top-level section of the YAML files
    /// (plus the roots listed in <c>altruist:config:env-roots</c>, plus <c>altruist</c>): the variable
    /// is the key path upper-cased, with <c>__</c> for <c>:</c> and <c>_</c> for <c>-</c>
    /// (<c>myapp:email:api-key</c> -&gt; <c>MYAPP__EMAIL__API_KEY</c>, see <see cref="EnvironmentVariableName"/>).
    /// A <c>_</c> stays a <c>_</c> where the YAML already has that key with an underscore.
    /// </para>
    /// <para>
    /// A YAML value may reference environment variables: <c>${NAME}</c> (empty when unset) or
    /// <c>${NAME:-default}</c> (the default when unset or empty); <c>$${</c> is a literal <c>${</c>.
    /// The expanded value takes the YAML value's place (environment overrides of the key still win).
    /// </para>
    /// <para>
    /// Files are read from <see cref="AppContext.BaseDirectory"/> (the build output folder) and are optional;
    /// YAML files reload on change. The result is cached process-wide: the first <see cref="Load"/> wins and later
    /// <c>args</c> are ignored. All framework code (DI, attributes) reads this same instance, so use it rather than building
    /// your own <see cref="IConfiguration"/>; inject <see cref="IConfiguration"/> in services.
    /// </para>
    /// </summary>
    public static class AppConfigLoader
    {
        /// <summary>Extra roots whose environment variables are mapped (for sections that are not in the YAML files).</summary>
        public const string EnvRootsKey = "altruist:config:env-roots";

        private const string AltruistRoot = "altruist";

        private static readonly object _lock = new();
        private static IConfiguration? _config;
        private static string[] _loadedArgs = Array.Empty<string>();

        /// <summary>
        /// Returns the process-wide configuration, building it on first call (thread-safe).
        /// </summary>
        /// <param name="args">Command-line arguments (<c>--altruist:server:http:port=8080</c> style), applied by the call that
        /// builds the configuration. Later calls may pass no arguments or the same ones.</param>
        /// <returns>The cached configuration root (or the instance supplied to <see cref="Set"/>).</returns>
        /// <exception cref="InvalidOperationException">The configuration was already built without these
        /// <paramref name="args"/> (something called <see cref="Load"/> before the entry point passed the command line):
        /// they would be silently ignored.</exception>
        public static IConfiguration Load(string[]? args = null)
        {
            if (_config is not null)
                return Cached(args);
            lock (_lock)
            {
                if (_config is not null)
                    return Cached(args);

                var env = AltruistEnvironment.Name;
                var basePath = AppContext.BaseDirectory;

                // Fallback for hostfxr context where BaseDirectory is empty
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = Environment.CurrentDirectory;
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = ".";

                var builder = new ConfigurationBuilder();
                var yaml = new ConfigurationBuilder();

                if (!string.IsNullOrWhiteSpace(basePath) && Directory.Exists(basePath))
                {
                    builder.SetBasePath(basePath);
                    foreach (var file in new[] { "config.yml", $"config.{env}.yml" })
                    {
                        builder.AddYamlFile(Path.Combine(basePath, file), optional: true, reloadOnChange: true);
                        yaml.AddYamlFile(Path.Combine(basePath, file), optional: true, reloadOnChange: false);
                    }
                }

                var yamlConfig = yaml.Build();
                var environment = Environment.GetEnvironmentVariables();
                var cfg = builder
                    .AddInMemoryCollection(ExpandPlaceholders(yamlConfig, environment))
                    .AddEnvironmentVariables(prefix: "ALTRUIST__")
                    // The prefix provider strips "ALTRUIST__", so ALTRUIST__SECURITY__KEY became
                    // "security:key" and never reached "altruist:security:key". Map every
                    // ROOT__A__B variable of a known root to root:a:b so env overrides the YAML keys.
                    .AddInMemoryCollection(MapEnvironment(yamlConfig, environment))
                    .AddCommandLine(args ?? Array.Empty<string>())
                    .Build();
                _loadedArgs = args ?? Array.Empty<string>();
                _config = cfg;
                return _config;
            }
        }

        private static IConfiguration Cached(string[]? args)
        {
            if (args is { Length: > 0 } && !args.SequenceEqual(_loadedArgs))
                throw new InvalidOperationException(
                    "The configuration was already loaded without these command-line arguments, so they would be ignored. " +
                    "Pass the arguments to the first AppConfigLoader.Load call (AltruistApplication.Run(args) at the very start of Main).");
            return _config!;
        }

        /// <summary>
        /// The environment variable that overrides a configuration path:
        /// <c>myapp:email:api-key</c> -&gt; <c>MYAPP__EMAIL__API_KEY</c>.
        /// </summary>
        /// <param name="path">Colon-separated configuration path.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static string EnvironmentVariableName(string path) =>
            (path ?? throw new ArgumentNullException(nameof(path))).ToUpperInvariant().Replace(":", "__").Replace('-', '_');

        /// <summary>
        /// Maps environment variables to configuration paths under the roots of <paramref name="yaml"/>
        /// (its top-level sections, <see cref="EnvRootsKey"/> and <c>altruist</c>). A segment's
        /// <c>_</c> becomes <c>-</c> unless <paramref name="yaml"/> has the underscore key at that
        /// place; <c>ALTRUIST__</c> variables also keep their verbatim path (earlier behaviour).
        /// </summary>
        /// <param name="yaml">The YAML-only configuration (used to discover roots and underscore keys).</param>
        /// <param name="environment">Environment variables, e.g. <see cref="Environment.GetEnvironmentVariables()"/>.</param>
        /// <returns>Configuration path / value pairs to add as an in-memory source.</returns>
        public static IEnumerable<KeyValuePair<string, string?>> MapEnvironment(IConfiguration yaml, IDictionary environment)
        {
            if (yaml is null) throw new ArgumentNullException(nameof(yaml));
            if (environment is null) throw new ArgumentNullException(nameof(environment));

            var roots = yaml.GetChildren().Select(c => c.Key)
                .Concat(yaml.GetSection(EnvRootsKey).GetChildren().Select(c => c.Value ?? ""))
                .Append(AltruistRoot)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (DictionaryEntry e in environment)
            {
                if (e.Key is not string key)
                    continue;
                foreach (var root in roots)
                {
                    var prefix = EnvironmentVariableName(root) + "__";
                    if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || key.Length == prefix.Length)
                        continue;

                    var segments = key[prefix.Length..].Split("__");
                    var path = root;
                    var verbatim = root;
                    foreach (var segment in segments)
                    {
                        var section = yaml.GetSection(path);
                        var dashed = segment.Replace('_', '-');
                        path += ":" + (segment != dashed && section.GetSection(segment).Exists() ? segment : dashed);
                        verbatim += ":" + segment;
                    }

                    yield return new KeyValuePair<string, string?>(path, e.Value as string);
                    if (string.Equals(root, AltruistRoot, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(path, verbatim, StringComparison.OrdinalIgnoreCase))
                        yield return new KeyValuePair<string, string?>(verbatim, e.Value as string);
                    break;
                }
            }
        }

        private static readonly Regex Placeholder = new(@"\$\$\{|\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}", RegexOptions.CultureInvariant);

        /// <summary>
        /// The values of <paramref name="yaml"/> that reference environment variables, expanded
        /// (see <see cref="ExpandPlaceholders(string, IDictionary)"/>).
        /// </summary>
        /// <param name="yaml">The YAML-only configuration.</param>
        /// <param name="environment">Environment variables.</param>
        /// <returns>Only the entries whose value contained <c>${</c>, with placeholders expanded.</returns>
        public static IEnumerable<KeyValuePair<string, string?>> ExpandPlaceholders(IConfiguration yaml, IDictionary environment)
        {
            if (yaml is null) throw new ArgumentNullException(nameof(yaml));
            if (environment is null) throw new ArgumentNullException(nameof(environment));
            foreach (var (key, value) in yaml.AsEnumerable())
            {
                if (value is not null && value.Contains("${", StringComparison.Ordinal))
                    yield return new KeyValuePair<string, string?>(key, ExpandPlaceholders(value, environment));
            }
        }

        /// <summary>
        /// Replaces <c>${NAME}</c> with the variable's value (empty when unset), <c>${NAME:-default}</c>
        /// with the value or, when unset or empty, the default, and <c>$${</c> with a literal <c>${</c>.
        /// </summary>
        /// <param name="value">Raw value containing placeholders.</param>
        /// <param name="environment">Variable lookup (keys are case-sensitive as given by the dictionary).</param>
        public static string ExpandPlaceholders(string value, IDictionary environment)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            if (environment is null) throw new ArgumentNullException(nameof(environment));
            return Placeholder.Replace(value, m =>
            {
                if (!m.Groups[1].Success)
                    return "${";
                var set = environment[m.Groups[1].Value] as string;
                return string.IsNullOrEmpty(set) && m.Groups[2].Success ? m.Groups[2].Value : set ?? "";
            });
        }

        /// <summary>
        /// Replaces the cached configuration returned by <see cref="Load"/>. Intended for tests and custom hosts; call it before
        /// bootstrap, since services built earlier keep the old instance.
        /// </summary>
        /// <param name="configuration">Configuration to use from now on.</param>
        public static void Set(IConfiguration configuration)
        {
            lock (_lock)
            {
                _config = configuration;
                _loadedArgs = Array.Empty<string>();
            }
        }

        /// <summary>Clears the cache so the next <see cref="Load"/> rebuilds from files, environment and the new args (tests).</summary>
        public static void Reset()
        {
            lock (_lock)
            { _config = null; }
        }
    }
}
