using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Forterro.Intelligence.Api.Resilience;

/// <summary>
/// Pipeline de resilience pour une charge liee a des ENTREES/SORTIES LONGUES.
///
/// Il existe parce que <c>AddBankingResilience</c> est inutilisable ici. Ce dernier
/// vit dans un bloc transverse mais ses valeurs sont propres au domaine bancaire :
/// 30 s de timeout global, 8 s par tentative. Une inference tuee a la 8e seconde
/// donne un symptome indiscernable d'un modele en panne.
///
/// Il n'est deliberement PAS remonte dans Forterro.BuildingBlocks : un seul service
/// s'en sert. Le generaliser maintenant, ce serait figer des valeurs calibrees sur un
/// unique cas d'usage — l'erreur exacte qui a produit AddBankingResilience.
///
/// Trois differences de fond avec un pipeline d'appel court :
///
///  1. Les delais se comptent en MINUTES. Le HttpClient est mis en timeout infini et
///     c'est Polly qui arbitre : sinon le HttpClient annule le premier, et la strategie
///     de reprise ne voit jamais l'echec qu'elle est censee traiter.
///  2. On rejoue PEU. Une inference coute cher en calcul ; trois reprises sur un
///     modele sature triplent la charge du seul composant deja en difficulte.
///  3. Le disjoncteur s'ouvre VITE et longtemps. Un modele indisponible le reste le
///     temps de se recharger en memoire ; le harceler ne fait que retarder sa reprise.
/// </summary>
public static class InferenceResilienceExtensions
{
    public static IHttpClientBuilder AddInferenceResilience(this IHttpClientBuilder builder, TimeSpan attemptTimeout)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Timeout infini cote HttpClient : Polly devient la seule autorite sur les
        // delais. Sans ca, le defaut de 100 s (ou celui configure) coupe la
        // connexion avant que la moindre strategie n'ait pu s'exprimer.
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        builder.AddResilienceHandler("inference", pipeline =>
        {
            // Plafond absolu : deux tentatives completes plus les attentes.
            pipeline.AddTimeout(new TimeoutStrategyOptions
            {
                Name = "total-timeout",
                Timeout = (attemptTimeout * 2) + TimeSpan.FromSeconds(30),
            });

            pipeline.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                Name = "retry",
                // UNE seule reprise. Au-dela, on double le cout d'une operation deja
                // chere sans rien apprendre de plus sur la disponibilite du modele.
                MaxRetryAttempts = 1,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                // Attente longue : un modele qui se charge en memoire met des
                // dizaines de secondes. Reprendre au bout de 300 ms echouerait
                // a coup sur, et consommerait l'unique reprise disponible.
                Delay = TimeSpan.FromSeconds(5),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError
                                       || r.StatusCode == HttpStatusCode.TooManyRequests),
            });

            pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                Name = "circuit-breaker",
                FailureRatio = 0.5,
                // Fenetre longue et seuil bas : avec des appels de plusieurs minutes,
                // le MinimumThroughput de 10 du pipeline bancaire ne serait jamais
                // atteint — le disjoncteur ne se declencherait tout simplement jamais.
                SamplingDuration = TimeSpan.FromMinutes(10),
                MinimumThroughput = 2,
                // Laisser le temps a un modele de se recharger avant de le resolliciter.
                BreakDuration = TimeSpan.FromMinutes(1),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError),
            });

            pipeline.AddTimeout(new TimeoutStrategyOptions
            {
                Name = "attempt-timeout",
                Timeout = attemptTimeout,
            });
        });

        return builder;
    }
}
