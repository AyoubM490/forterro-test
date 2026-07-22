namespace Forterro.BuildingBlocks.Messaging;

/// <summary>
/// Pattern Inbox. Kafka garantit at-least-once : un evenement PEUT etre relivre
/// (rebalance, redemarrage avant commit d'offset). Sur un flux de paiement,
/// rejouer deux fois "debiter 1 200 EUR" n'est pas une option.
/// On enregistre donc l'EventId consomme dans la MEME transaction que l'effet metier.
/// </summary>
public interface IProcessedEventStore
{
    /// <summary>Retourne true si l'evenement a deja ete traite avec succes.</summary>
    Task<bool> HasBeenProcessedAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>Marque l'evenement comme traite. Doit etre commite avec l'effet metier.</summary>
    Task MarkAsProcessedAsync(Guid eventId, string contractName, CancellationToken cancellationToken);
}
