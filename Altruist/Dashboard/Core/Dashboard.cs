using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Altruist.Dashboard
{
    /// <summary>
    /// IStartupFilter that wires the Angular dashboard into the existing HTTP pipeline,
    /// on the SAME host/port, under a configurable base path.
    ///
    /// The Angular app is embedded as resources under "Altruist.Dashboard.UI.dist"
    /// in this assembly. Consumers do NOT configure any dist folder; they just
    /// set altruist:dashboard:enabled = true and (optionally) altruist:dashboard:basePath.
    /// </summary>
    [Service]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    public sealed class DashboardStartupFilter : IStartupFilter
    {
        private readonly string _basePath;

        public DashboardStartupFilter(
            [AppConfigValue("altruist:dashboard:basePath", "/dashboard")] string basePath)
        {
            _basePath = NormalizePath(basePath, "/dashboard");
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                const string embeddedRoot = "Altruist.Dashboard.UI.dist";

                var assembly = typeof(DashboardStartupFilter).Assembly;
                var fileProvider = new ManifestEmbeddedFileProvider(assembly, embeddedRoot);

                var indexFile = fileProvider.GetFileInfo("index.html");
                if (!indexFile.Exists)
                {
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
                            var idx = fileProvider.GetFileInfo("index.html");
                            if (!idx.Exists)
                            {
                                context.Response.StatusCode = StatusCodes.Status404NotFound;
                                await context.Response.WriteAsync("Dashboard index.html not found.");
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


        private static string NormalizePath(string? path, string defaultIfEmpty)
        {
            var p = string.IsNullOrWhiteSpace(path) ? defaultIfEmpty : path!.Trim();
            if (!p.StartsWith("/"))
                p = "/" + p;
            if (p.Length > 1 && p.EndsWith("/"))
                p = p.TrimEnd('/');
            return p;
        }
    }
}
