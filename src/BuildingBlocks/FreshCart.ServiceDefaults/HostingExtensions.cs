using System.Net.Http;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FreshCart.ServiceDefaults;

/// <summary>
/// One-line bootstrap that every FreshCart service calls from its <c>Program.cs</c> to inherit the
/// same observability, service-discovery, resilience and health-check defaults.
/// </summary>
public static class HostingExtensions
{
    private const string LivenessTag = "live";
    private const string ReadinessTag = "ready";

    /// <summary>
    /// Adds OpenTelemetry, default health checks, service discovery and resilience to the host builder.
    /// </summary>
    public static TBuilder AddFreshCartServiceDefaults<TBuilder>(this TBuilder hostBuilder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);

        hostBuilder.ConfigureOpenTelemetry();
        hostBuilder.AddDefaultHealthChecks();

        hostBuilder.Services.AddServiceDiscovery();
        hostBuilder.Services.ConfigureHttpClientDefaults(httpClientBuilder =>
        {
            httpClientBuilder.AddStandardResilienceHandler();
            httpClientBuilder.AddServiceDiscovery();

            // Internal service-to-service calls run over HTTPS with the ASP.NET Core development
            // certificate locally; accept it (and only it) on the inner transport in Development so
            // those hops do not fail with "UntrustedRoot". Production keeps full chain validation.
            if (hostBuilder.Environment.IsDevelopment())
            {
                httpClientBuilder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    SslOptions =
                    {
                        RemoteCertificateValidationCallback =
                            DevelopmentCertificateValidation.AcceptAspNetCoreDevelopmentCertificate,
                    },
                });
            }
        });

        return hostBuilder;
    }

    /// <summary>
    /// Wires logging, metrics and tracing through OpenTelemetry. Exports OTLP when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured (Aspire and AKS both set this automatically).
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder hostBuilder)
        where TBuilder : IHostApplicationBuilder
    {
        hostBuilder.Logging.AddOpenTelemetry(loggingOptions =>
        {
            loggingOptions.IncludeFormattedMessage = true;
            loggingOptions.IncludeScopes = true;
            loggingOptions.ParseStateValues = true;
        });

        hostBuilder.Services
            .AddOpenTelemetry()
            .WithMetrics(metricsBuilder => metricsBuilder
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("FreshCart.*"))
            .WithTracing(tracingBuilder => tracingBuilder
                .AddSource(hostBuilder.Environment.ApplicationName)
                .AddSource("FreshCart.*")
                .AddSource("MassTransit")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddGrpcClientInstrumentation());

        AddOpenTelemetryExporters(hostBuilder);
        return hostBuilder;
    }

    /// <summary>
    /// Registers a basic liveness check tagged <c>live</c>. Services add their own readiness checks
    /// (database, broker, cache) with the <c>ready</c> tag so the readiness endpoint reflects real
    /// dependency state.
    /// </summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder hostBuilder)
        where TBuilder : IHostApplicationBuilder
    {
        hostBuilder.Services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LivenessTag]);

        return hostBuilder;
    }

    /// <summary>
    /// Maps the standard health endpoints. <c>/alive</c> and <c>/ready</c> are mapped in every
    /// environment: the Kubernetes liveness/readiness probes and the Docker HEALTHCHECK call them
    /// unconditionally, and a pod whose probes return 404 is killed and restarted in a loop.
    /// The full-detail <c>/health</c> endpoint stays Development/Staging-only because its payload
    /// enumerates every dependency (database, broker, cache) and would leak internal topology
    /// from a production cluster.
    /// </summary>
    public static WebApplication MapDefaultHealthEndpoints(this WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LivenessTag),
        });

        application.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadinessTag),
        });

        if (application.Environment.IsDevelopment() || application.Environment.IsStaging())
        {
            application.MapHealthChecks("/health", new HealthCheckOptions
            {
                ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
            });
        }

        return application;
    }

    private static void AddOpenTelemetryExporters<TBuilder>(TBuilder hostBuilder)
        where TBuilder : IHostApplicationBuilder
    {
        var endpoint = hostBuilder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            hostBuilder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }
}
