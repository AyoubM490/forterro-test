using System.Text.Json.Serialization;

namespace Forterro.BuildingBlocks.Messaging;

/// <summary>
/// Contrat de base de tout evenement publie sur le bus.
/// Immuable, versionne, porteur de son identite : c'est ce qui rend l'idempotence
/// possible cote consommateur (dedup sur <see cref="EventId"/>).
/// </summary>
public abstract record IntegrationEvent
{
    /// <summary>Identifiant unique de l'evenement. Cle de deduplication cote consommateur.</summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>Horodatage de production (UTC).</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Version du schema. On ne casse jamais un contrat : on publie une V2 en parallele
    /// et on retire la V1 quand tous les consommateurs ont migre.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Cle de partitionnement Kafka. Garantit l'ordre par agregat.</summary>
    [JsonIgnore]
    public abstract string PartitionKey { get; }
}
