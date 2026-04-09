// Altruist/ConfigLoader.cs
using System.Reflection;

using Microsoft.Extensions.Configuration;

namespace Altruist
{
    public static class AppConfigLoader
    {
        private static readonly object _lock = new();
        private static IConfiguration? _config;

        public static IConfiguration Load(string[]? args = null)
        {
            if (_config is not null)
                return _config;
            lock (_lock)
            {
                if (_config is not null)
                    return _config;

                var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
                var basePath = AppContext.BaseDirectory;

                // Fallback for hostfxr context where BaseDirectory is empty
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = Environment.CurrentDirectory;
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = ".";

                var builder = new ConfigurationBuilder();

                if (!string.IsNullOrWhiteSpace(basePath) && Directory.Exists(basePath))
                {
                    builder.SetBasePath(basePath);
                    builder.AddYamlFile(Path.Combine(basePath, "config.yml"), optional: true, reloadOnChange: true);
                    builder.AddYamlFile(Path.Combine(basePath, $"config.{env}.yml"), optional: true, reloadOnChange: true);
                }

                var cfg = builder
                    .AddEnvironmentVariables(prefix: "ALTRUIST__")
                    .AddCommandLine(args ?? Array.Empty<string>())
                    .Build();

                _config = cfg;
                return _config;
            }
        }

        public static void Set(IConfiguration configuration)
        {
            lock (_lock)
            { _config = configuration; }
        }

        public static void Reset()
        {
            lock (_lock)
            { _config = null; }
        }
    }
}
