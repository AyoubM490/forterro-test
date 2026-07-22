using Forterro.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Forterro.BuildingBlocks.Persistence;

public static class MessagingModelBuilderExtensions
{
    /// <summary>
    /// Declare les tables Outbox et Inbox dans le modele du service.
    /// A appeler depuis OnModelCreating : la brique fournit le schema, le service le possede.
    /// </summary>
    public static ModelBuilder ApplyMessagingModel(this ModelBuilder modelBuilder, string schema = "messaging")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", schema);
            b.HasKey(m => m.Id);

            b.Property(m => m.ContractName).HasMaxLength(200).IsRequired();
            b.Property(m => m.Topic).HasMaxLength(200).IsRequired();
            b.Property(m => m.PartitionKey).HasMaxLength(200).IsRequired();
            b.Property(m => m.Payload).IsRequired();
            b.Property(m => m.LeasedBy).HasMaxLength(200);
            b.Property(m => m.LastError).HasMaxLength(2000);
            b.Property(m => m.TraceParent).HasMaxLength(100);

            // Concurrence optimiste : deux replicas ne prennent pas le meme lot.
            b.Property(m => m.Version).IsConcurrencyToken();

            // Index couvrant la requete du dispatcher (filtre partiel : seuls les non publies).
            b.HasIndex(m => new { m.ProcessedAt, m.LeasedUntil, m.OccurredAt })
                .HasDatabaseName("ix_outbox_pending")
                .HasFilter("processed_at IS NULL");
        });

        modelBuilder.Entity<ProcessedEvent>(b =>
        {
            b.ToTable("processed_events", schema);
            b.HasKey(e => e.EventId);
            b.Property(e => e.ContractName).HasMaxLength(200).IsRequired();
            b.HasIndex(e => e.ProcessedAt).HasDatabaseName("ix_processed_events_at");
        });

        return modelBuilder;
    }
}
