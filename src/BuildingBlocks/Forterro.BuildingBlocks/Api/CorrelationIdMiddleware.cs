using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace Forterro.BuildingBlocks.Api;

/// <summary>
/// Reprend le X-Correlation-Id du client (ou en cree un), le pousse dans les logs
/// et le renvoie dans la reponse. Sur un appel qui traverse Invoicing -> Kafka -> Payments,
/// c'est ce qui permet au support de tout retrouver a partir d'un seul identifiant.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        }

        context.Items[HeaderName] = correlationId;
        Activity.Current?.SetTag("forterro.correlation_id", correlationId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // Pas de propriete TraceId ici : le formateur Serilog ecrit deja le TraceId de
        // l'Activity courante dans @tr (et le SpanId dans @sp). L'ajouter en propriete
        // produisait deux champs distincts portant la meme valeur, indexes deux fois,
        // avec le risque qu'un tableau de bord filtre sur celui des deux qui n'est pas
        // alimente quand aucune Activity n'est active.
        //
        // CorrelationId reste : il n'est PAS redondant. Il peut venir du client par
        // l'en-tete X-Correlation-Id et relier plusieurs traces d'un meme parcours
        // fonctionnel, la ou @tr ne couvre qu'une seule trace.
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
