using System.Net;

namespace OssmSharp.Web.GuestWeb;

/// <summary>
/// Configures the restricted guest web application.
/// </summary>
public static class GuestApplication
{
    /// <summary>
    /// Builds the loopback guest application and maps its interactive control route.
    /// </summary>
    /// <param name="builder">The configured web application builder.</param>
    /// <returns>The guest application, ready to run and dispose.</returns>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        builder.Services.AddGuestConfiguration().AddLocalBlazor().AddGuestSession();
        builder.Services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<GuestOptions>>((server, options) =>
            {
                server.Listen(IPAddress.Loopback, options.Value.GuestPort);
                server.Limits.MaxRequestBodySize = 16_384;
            });
        builder.WebHost.UseStaticWebAssets();

        var app = builder.Build();
        app.UseWebSockets();
        app.UseGuestAccessPolicy(app.Services.GetRequiredService<IOptions<GuestOptions>>().Value);
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapDefaultEndpoints();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options =>
        {
            options.ContentSecurityFrameAncestorsPolicy = "'none'";
        });
        return app;
    }
}
