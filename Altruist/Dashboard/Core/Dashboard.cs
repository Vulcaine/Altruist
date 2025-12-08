using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Altruist.Dashboard
{
    [Service(typeof(IStartupFilter), lifetime: ServiceLifetime.Transient)]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    public sealed class DashboardStartupFilter : IStartupFilter
    {
        private readonly string _basePath;

        public DashboardStartupFilter()
        {
            _basePath = "/altruist/dashboard";
        }

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
