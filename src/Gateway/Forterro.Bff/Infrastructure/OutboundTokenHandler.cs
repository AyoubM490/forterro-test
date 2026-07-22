using Forterro.Bff.Proxy;
using Forterro.BuildingBlocks.Api;

namespace Forterro.Bff.Infrastructure;

/// <summary>
/// Pose le jeton de l'appelant sur les appels sortants de l'agregation.
///
/// Le proxy YARP a son propre transform ; ce handler couvre l'autre moitie du BFF, celle ou le
/// BFF appelle lui-meme plusieurs services. Les deux resolvent le jeton par le meme chemin
/// (<see cref="OutboundToken"/>) : deux logiques de propagation qui divergent, c'est la
/// garantie qu'un jour l'une des deux transmettra les mauvais droits.
/// </summary>
internal sealed class OutboundTokenHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = accessor.HttpContext;

        if (context is not null)
        {
            request.Headers.Authorization = await OutboundToken.ResolveAsync(context);

            // On lit l'identifiant depuis Items, pas depuis l'en-tete entrant : le middleware
            // en a genere un si l'appelant n'en fournissait pas. Sans cette propagation, le
            // support ne peut pas relier les deux appels agreges a la requete d'origine.
            if (context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var correlationId)
                && correlationId is string value)
            {
                request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, value);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
