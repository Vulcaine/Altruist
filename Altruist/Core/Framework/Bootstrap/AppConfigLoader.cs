// Altruist/ConfigLoader.cs
using Microsoft.Extensions.Configuration;

namespace Altruist
{
    public static class AppConfigLoader
    {
        private static readonly object _lock = new();
        private static IConfiguration? _config;

        public static IConfiguration Load(string[]? args = null)
        {
            if (_config is not null) return _config;
            lock (_lock)
            {
                if (_config is not null) return _config;

                var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
                var basePath = AppContext.BaseDirectory;

                var cfg = new ConfigurationBuilder()
                    .SetBasePath(basePath)
                    .AddYamlFile(Path.Combine(basePath, "config.yml"), optional: false, reloadOnChange: true)
                    .AddYamlFile(Path.Combine(basePath, $"config.{env}.yml"), optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables(prefix: "ALTRUIST__")
                    .AddCommandLine(args ?? Array.Empty<string>())
                    .Build();

                _config = cfg;
                return _config;
            }
        }

        public static void Set(IConfiguration configuration)
        {
            lock (_lock) { _config = configuration; }
        }

        public static void Reset()
        {
            lock (_lock) { _config = null; }
        }
    }
}
