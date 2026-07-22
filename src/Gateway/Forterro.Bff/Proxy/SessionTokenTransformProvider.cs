using Forterro.Bff.Security;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Forterro.Bff.Proxy;

/// <summary>
/// Transforme la requete sortante : le cookie devient un jeton porteur.
///
/// C'est la charniere du BFF. Cote navigateur entre un cookie opaque ; cote service sort un
/// <c>Authorization: Bearer</c> standard. Les services metier n'ont donc rien a savoir des
/// sessions : ils restent de purs serveurs de ressources OAuth, appelables aussi bien par le
/// BFF que par une autre ligne produit.
/// </summary>
internal sealed class SessionTokenTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context)
    {
    }

    public void ValidateCluster(TransformClusterValidationContext context)
    {
    }

    public void Apply(TransformBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddRequestTransform(async transform =>
        {
            // Le cookie de session n'a aucun sens en aval et ne doit pas y parvenir : un service
            // metier qui le journaliserait ecrirait de quoi rejouer la session dans ses logs.
            transform.ProxyRequest.Headers.Remove("Cookie");
            transform.ProxyRequest.Headers.Remove(AntiForgeryMiddleware.HeaderName);

            transform.ProxyRequest.Headers.Authorization =
                await OutboundToken.ResolveAsync(transform.HttpContext);
        });
    }
}
