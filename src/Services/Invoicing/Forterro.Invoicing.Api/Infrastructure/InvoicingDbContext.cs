using Forterro.BuildingBlocks.Outbox;
using Forterro.BuildingBlocks.Persistence;
using Forterro.Invoicing.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Forterro.Invoicing.Api.Infrastructure;

public sealed class InvoicingDbContext(DbContextOptions<InvoicingDbContext> options)
    : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InvoiceSequence> InvoiceSequences => Set<InvoiceSequence>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("invoicing");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InvoicingDbContext).Assembly);

        // Tables Outbox / Inbox fournies par la brique partagee.
        modelBuilder.ApplyMessagingModel(schema: "invoicing");

        // Applique en dernier : convertit tout le modele en snake_case.
        modelBuilder.UseSnakeCaseNames();
    }
}
