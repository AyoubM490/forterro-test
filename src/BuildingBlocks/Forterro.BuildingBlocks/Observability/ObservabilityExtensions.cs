using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Forterro.BuildingBlocks.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Logs structures (JSON) + traces + metriques, exportes en OTLP.
    ///
    /// Le point cle sur une architecture distribuee : le TraceId est injecte dans les logs.
    /// Un incident se debogue en partant d'une ligne de log vers la trace complete
    /// HTTP -> Outbox -> Kafka -> saga, a travers trois services.
    /// </summary>
    public static IHostApplicationBuilder AddForterroObservability(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var otlpEndpoint = builder.Configuration["Otlp:Endpoint"];

        builder.Services.AddSerilog((sp, config) => config
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(sp)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service.name", serviceName)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            // RenderedCompactJsonFormatter et non CompactJsonFormatter : le premier
            // ecrit le message RENDU dans @m, le second n'ecrit que le gabarit dans @mt
            // (« Health check {HealthCheckName} ... », placeholders non substitues).
            // Dans un agregateur, un message non rendu est illisible et non cherchable.
            //
            // On ne perd pas le regroupement par type d'evenement : les deux formats
            // emettent @i, l'empreinte du gabarit, identique pour toutes les occurrences
            // d'un meme evenement. C'est @i qui repond a « combien de fois CET evenement »,
            // pas le texte du message.
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceNamespace: Telemetry.ServiceNamespace, serviceVersion: "1.0.0")
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)]);

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddSource(Telemetry.SourceName)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Les sondes Kubernetes generent 10 traces/seconde de pur bruit.
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                        o.RecordException = true;
                    })
                    .AddHttpClientInstrumentation(o => o.RecordException = true);

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    .AddMeter(Telemetry.SourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return builder;
    }
}
