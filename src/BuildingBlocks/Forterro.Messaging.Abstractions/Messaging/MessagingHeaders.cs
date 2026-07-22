namespace Forterro.BuildingBlocks.Messaging;

/// <summary>
/// Noms des en-tetes de message, independants du transport.
///
/// Ils vivaient dans KafkaEventPublisher, ce qui obligeait l'Outbox — pourtant
/// agnostique du transport, il ne connait que <see cref="IEventPublisher"/> — a
/// referencer l'implementation Kafka pour ecrire « x-event-id ». Une inversion de
/// couche : la brique generique dependait de la brique specialisee.
///
/// Ces noms n'ont rien de propre a Kafka. « traceparent » vient du W3C Trace Context,
/// les autres sont des conventions maison. Un transport RabbitMQ ou Azure Service Bus
/// utiliserait exactement les memes.
/// </summary>
public static class MessagingHeaders
{
    /// <summary>Nom logique et versionne du contrat, ex. « invoicing.invoice-issued.v1 ».</summary>
    public const string ContractName = "x-contract-name";

    /// <summary>Identifiant de l'evenement, cle de deduplication cote Inbox.</summary>
    public const string EventId = "x-event-id";

    /// <summary>Contexte de trace W3C, ce qui relie le producteur au consommateur.</summary>
    public const string TraceParent = "traceparent";

    /// <summary>Correlation fonctionnelle, potentiellement fournie par l'appelant.</summary>
    public const string Correlation = "x-correlation-id";
}
