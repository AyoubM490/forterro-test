namespace Forterro.BuildingBlocks.Outbox;

/// <summary>
/// Message en attente de publication.
///
/// Le probleme resolu : "j'ecris la facture en base ET je publie sur Kafka" ne peut pas
/// etre atomique (deux systemes, pas de transaction distribuee). Si on publie d'abord et
/// que le commit SQL echoue, on a annonce un evenement qui n'a jamais eu lieu.
/// Solution : on ecrit l'evenement dans CETTE table, dans la meme transaction que le metier.
/// Un dispatcher le relaie ensuite vers le broker (at-least-once, l'inbox gere le doublon).
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Nom logique du contrat (pas le nom de type .NET).</summary>
    public string ContractName { get; set; } = string.Empty;

    public string Topic { get; set; } = string.Empty;

    public string PartitionKey { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    /// <summary>Bail pose par une instance du dispatcher. Evite le double envoi entre replicas.</summary>
    public DateTimeOffset? LeasedUntil { get; set; }

    public string? LeasedBy { get; set; }

    /// <summary>Jeton de concurrence optimiste : deux replicas ne peuvent pas prendre le meme lot.</summary>
    public int Version { get; set; }

    /// <summary>Contexte de trace W3C, pour rattacher la publication a la requete d'origine.</summary>
    public string? TraceParent { get; set; }
}
