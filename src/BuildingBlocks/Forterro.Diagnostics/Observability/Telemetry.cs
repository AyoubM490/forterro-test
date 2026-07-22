using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Forterro.BuildingBlocks.Observability;

/// <summary>Sources de traces et de metriques partagees par tous les Business Services.</summary>
public static class Telemetry
{
    public const string ServiceNamespace = "forterro.business-services";
    public const string SourceName = "Forterro.BusinessServices";

    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");

    public static readonly Meter Meter = new(SourceName, "1.0.0");

    /// <summary>Metrique metier, pas technique : c'est elle qui alerte le support avant le client.</summary>
    public static readonly Counter<long> BusinessEvents =
        Meter.CreateCounter<long>(
            "forterro.business_events",
            unit: "{event}",
            description: "Evenements metier emis, ventiles par contrat et resultat.");

    public static readonly Histogram<double> ExternalCallDuration =
        Meter.CreateHistogram<double>(
            "forterro.external_call.duration",
            unit: "ms",
            description: "Duree des appels sortants vers les partenaires bancaires.");
}
