// Altruist/Bootstrap.cs
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public static class AltruistBootstrap
{
    /// <summary>
    /// Entry point called by your app. Add more steps after BootstrapServices as needed.
    /// </summary>
    public static void Bootstrap(IServiceCollection services)
    {
        BootstrapServices(services);

        // TODO: your additional bootstrap steps go here
        // e.g., feature wiring, configuration, etc.
    }

    /// <summary>
    /// Scans loaded assemblies for types annotated with [Service] and registers them.
    /// Uses a console logger by default, or the app's configured ILoggerFactory if available.
    /// </summary>
    public static void BootstrapServices(IServiceCollection services)
    {
        new AltruistConfig().Configure(services);
    }

}
