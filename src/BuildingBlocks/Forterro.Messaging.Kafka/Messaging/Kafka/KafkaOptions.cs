using System.ComponentModel.DataAnnotations;

namespace Forterro.BuildingBlocks.Messaging.Kafka;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    [Required]
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Groupe de consommation. Un groupe par service, jamais partage.</summary>
    public string ConsumerGroupId { get; set; } = string.Empty;

    /// <summary>Topics ecoutes par ce service.</summary>
    public IList<string> Topics { get; } = [];

    /// <summary>
    /// acks=all + idempotence producteur : pas de perte, pas de doublon cote broker.
    /// C'est le minimum sur un flux de facturation.
    /// </summary>
    public bool EnableIdempotence { get; set; } = true;

    /// <summary>Nombre de tentatives avant bascule en dead-letter.</summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    public string DeadLetterTopicSuffix { get; set; } = ".dlq";

    /// <summary>SASL/SSL en production (MSK, Confluent Cloud). Vide en local.</summary>
    public string? SecurityProtocol { get; set; }

    public string? SaslMechanism { get; set; }

    public string? SaslUsername { get; set; }

    public string? SaslPassword { get; set; }
}
