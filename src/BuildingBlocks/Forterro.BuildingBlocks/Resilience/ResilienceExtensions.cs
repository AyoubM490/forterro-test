using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Forterro.BuildingBlocks.Resilience;

public static class ResilienceExtensions
{
    /// <summary>
    /// Pipeline de resilience pour les appels sortants vers un partenaire bancaire.
    ///
    /// Ordre voulu (le plus externe en premier) :
    ///   timeout global -> retry -> circuit breaker -> timeout par tentative
    /// Un retry place a l'exterieur du breaker rejouerait sur un circuit ouvert :
    /// on ferait tomber le partenaire au lieu de le laisser respirer.
    ///
    /// Point critique metier : on ne rejoue JAMAIS un POST d'initiation de paiement
    /// sans cle d'idempotence. La politique de retry ne cible donc que les erreurs
    /// de transport et les 5xx, et le client envoie systematiquement un Idempotency-Key.
    /// </summary>
    public static IHttpClientBuilder AddBankingResilience(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddResilienceHandler("banking", pipeline =>
        {
            pipeline.AddTimeout(new TimeoutStrategyOptions
            {
                Name = "total-timeout",
                Timeout = TimeSpan.FromSeconds(30),
            });

            pipeline.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                Name = "retry",
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(300),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError
                                       || r.StatusCode == HttpStatusCode.RequestTimeout
                                       || r.StatusCode == HttpStatusCode.TooManyRequests),
            });

            pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                Name = "circuit-breaker",
                // 50 % d'echec sur une fenetre de 30 s, avec au moins 10 appels :
                // en dessous, un pic de 2 erreurs sur 3 appels ouvrirait le circuit pour rien.
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError),
            });

            pipeline.AddTimeout(new TimeoutStrategyOptions
            {
                Name = "attempt-timeout",
                Timeout = TimeSpan.FromSeconds(8),
            });
        });

        return builder;
    }
}
