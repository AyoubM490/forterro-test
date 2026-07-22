namespace Forterro.BuildingBlocks.Messaging;

/// <summary>
/// Publication directe sur le broker.
/// ATTENTION : dans un handler metier on ne l'appelle pas directement, on passe par
/// l'Outbox (<see cref="Outbox.IOutboxWriter"/>) pour rester atomique avec le commit SQL.
/// Ce contrat est destine au dispatcher d'Outbox et aux tests.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default);

    /// <param name="parentTraceParent">
    /// Contexte de trace W3C de l'operation qui a PRODUIT l'evenement, au format
    /// traceparent. Indispensable depuis l'Outbox : le dispatcher publie dans une boucle
    /// de fond, des secondes apres la requete d'origine et sans aucun lien avec elle.
    /// Sans ce parent, la trace repart de zero a la publication et le consommateur est
    /// rattache au dispatcher au lieu de l'appel utilisateur.
    /// </param>
    Task PublishRawAsync(
        string topic,
        string contractName,
        string partitionKey,
        string payloadJson,
        IReadOnlyDictionary<string, string>? headers = null,
        string? parentTraceParent = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Traitement d'un evenement entrant. Implemente par chaque service consommateur.</summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken);
}
