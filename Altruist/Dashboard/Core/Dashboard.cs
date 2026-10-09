using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Altruist.Dashboard
{
    /// <summary>
    /// Serves the embedded dashboard single-page app at <c>/altruist/dashboard</c>. This is the entry point of the
    /// Altruist dashboard: a browser UI over the JSON APIs under <c>/dashboard/v1/*</c> (summary, sessions, cache,
    /// vaults, network, performance, API lab).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enable by referencing the <c>Altruist.Dashboard</c> package and setting <c>altruist:dashboard:enabled</c> to
    /// <c>true</c>; this filter is then registered as a transient <see cref="IStartupFilter"/> and the gated
    /// controllers are mapped. Static UI files are served from the assembly's embedded resources under the base path,
    /// and any extension-less path under it returns <c>index.csr.html</c> (client-side routing). If the UI was not
    /// embedded at build time the filter adds nothing.
    /// </para>
    /// <para>
    /// Security: neither the UI nor the <c>/dashboard/v1</c> APIs apply authentication or a development-only check;
    /// the APIs expose configuration values, connection IPs, cache contents and raw SQL execution. Enable it only in
    /// development or behind your own network/auth protection.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// # config.yml
    /// altruist:
    ///   dashboard:
    ///     enabled: true
    /// # then open http://localhost:&lt;port&gt;/altruist/dashboard
    /// </code>
    /// </example>
    [Service(typeof(IStartupFilter), lifetime: ServiceLifetime.Transient)]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    public sealed class DashboardStartupFilter : IStartupFilter
    {
        private readonly string _basePath;

        /// <summary>Creates the filter with the fixed base path <c>/altruist/dashboard</c>.</summary>
        public DashboardStartupFilter()
        {
            _basePath = "/altruist/dashboard";
        }

        /// <summary>
        /// Adds the static-file and SPA-fallback middleware ahead of the rest of the pipeline, then calls <paramref name="next"/>.
        /// </summary>
        /// <param name="next">The remaining pipeline configuration.</param>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                var assembly = typeof(DashboardStartupFilter).Assembly;
                var fileProvider = new EmbeddedFileProvider(assembly, baseNamespace: string.Empty);

                const string indexPath = "index.csr.html";

                var indexFile = fileProvider.GetFileInfo(indexPath);
                if (!indexFile.Exists)
                {
                    // No embedded UI => skip dashboard
                    next(app);
                    return;
                }

                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = fileProvider,
                    RequestPath = _basePath
                });

                app.MapWhen(
                    ctx =>
                    {
                        var path = ctx.Request.Path.Value ?? "";
                        return path.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase)
                               && !Path.HasExtension(path);
                    },
                    spaApp =>
                    {
                        spaApp.Run(async context =>
                        {
                            var idx = fileProvider.GetFileInfo(indexPath);
                            if (!idx.Exists)
                            {
                                context.Response.StatusCode = StatusCodes.Status404NotFound;
                                await context.Response.WriteAsync("Dashboard index not found.");
                                return;
                            }

                            context.Response.ContentType = "text/html; charset=utf-8";
                            await using var stream = idx.CreateReadStream();
                            await stream.CopyToAsync(context.Response.Body);
                        });
                    });

                next(app);
            };
        }
    }
}
