using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OssmSharp.Web.ServiceDefaults.Models;

namespace OssmSharp.Web.ServiceDefaults;

/// <summary>
/// Configures web hosting defaults and development health endpoints.
/// </summary>
public static class BuilderExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// Registers shared telemetry and health checks.
    /// </summary>
    /// <param name="builder">The application host builder.</param>
    /// <returns>The configured host builder.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation())
            .WithTracing(tracing =>
            {
                tracing.AddSource("Microsoft.AspNetCore.SignalR.Client", "Microsoft.AspNetCore.SignalR.Server")
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health-check requests from traces.
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath));
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            telemetry.UseOtlpExporter();

        builder.AddDefaultHealthChecks();

        return builder;
    }

    /// <summary>
    /// Registers readiness and liveness health checks.
    /// </summary>
    /// <param name="builder">The application host builder.</param>
    /// <returns>The configured host builder.</returns>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Report the app live when it can respond to health checks.
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps readiness and liveness endpoints only in development.
    /// </summary>
    /// <param name="app">The web application to configure.</param>
    /// <returns>The configured web application.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Exposing health endpoints outside development can reveal service state.
        // Review https://aka.ms/dotnet/aspire/healthchecks before enabling them.
        if (app.Environment.IsDevelopment())
        {
            // Readiness requires every registered health check to pass.
            app.MapHealthChecks(HealthEndpointPath);

            // Liveness requires health checks tagged "live" to pass.
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live"),
            });
        }

        return app;
    }

    /// <summary>
    /// Rejects non-loopback requests and disallowed origins, and sets response security headers.
    /// </summary>
    /// <param name="app">The local web application's middleware pipeline.</param>
    /// <param name="port">The port expected in the loopback host and origin.</param>
    public static void UseLocalAccessPolicy(this IApplicationBuilder app, int port)
        => UseAccessPolicy(app, port, null);

    /// <summary>
    /// Allows local access and access through the configured HTTPS reverse proxy.
    /// </summary>
    /// <param name="app">The guest app's request pipeline.</param>
    /// <param name="options">The validated guest port and public URL settings.</param>
    /// <remarks>
    /// Trusts <c>X-Forwarded-Proto</c> from one proxy at <c>127.0.0.1</c>.
    /// Forwarded host names, user identities, and client IP addresses are ignored.
    /// </remarks>
    public static void UseGuestAccessPolicy(this IApplicationBuilder app, GuestOptions options)
    {
        var publicOrigin = options.Hostname.Length == 0 ? null : new Uri(options.Hostname).GetLeftPart(UriPartial.Authority);
        if (publicOrigin is not null)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            forwarded.KnownProxies.Add(IPAddress.Loopback);
            app.UseForwardedHeaders(forwarded);
        }
        UseAccessPolicy(app, options.GuestPort, publicOrigin);
    }

    private static void UseAccessPolicy(IApplicationBuilder app, int port, string? publicOrigin)
    {
        app.UseExceptionHandler();
        var host = $"127.0.0.1:{port}";
        var origin = $"http://{host}";
        var publicHost = publicOrigin is null ? null : new Uri(publicOrigin).Authority;
        var policy = $"default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' ws://{host}; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
        var publicPolicy = publicOrigin is null ? null : policy.Replace($"ws://{host}", $"wss://{publicHost}", StringComparison.Ordinal);
        app.Use(async (context, next) =>
        {
            var local = string.Equals(context.Request.Host.Value, host, StringComparison.OrdinalIgnoreCase);
            var proxied = publicHost is not null
                && string.Equals(context.Request.Host.Value, publicHost, StringComparison.OrdinalIgnoreCase)
                && context.Request.IsHttps;
            if ((!local && !proxied)
                || context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var suppliedOrigin = context.Request.Headers.Origin.ToString();
            var needsOrigin = context.WebSockets.IsWebSocketRequest ||
                context.Request.Method is not ("GET" or "HEAD" or "OPTIONS");
            var expectedOrigin = local ? origin : publicOrigin;
            if ((needsOrigin || suppliedOrigin.Length > 0) && !suppliedOrigin.Equals(expectedOrigin, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Content-Security-Policy"] = local ? policy : publicPolicy;
            await next(context);
        });
    }

}
