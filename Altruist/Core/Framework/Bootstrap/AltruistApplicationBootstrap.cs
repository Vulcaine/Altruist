// Altruist.Boot/AltruistApplication.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist
{
    public static class AltruistApplication
    {
        public static IConfiguration Configuration { get; private set; } = default!;

        public static async Task Run(string[]? args = null)
        {
            var cfg = AppConfigLoader.Load(args);
            Configuration = cfg;

            AltruistBootstrap.Services.AddSingleton(cfg);
            await AltruistBootstrap.Bootstrap();
        }
    }
}
