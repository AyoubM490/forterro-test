using Forterro.BuildingBlocks.Outbox;
using Forterro.BuildingBlocks.Persistence;
using Forterro.Payments.Worker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forterro.Payments.Worker.Infrastructure;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options)
    : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public DbSet<PaymentSaga> Sagas => Set<PaymentSaga>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("payments");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);
        modelBuilder.ApplyMessagingModel(schema: "payments");
        modelBuilder.UseSnakeCaseNames();
    }
}

public sealed class PaymentSagaConfiguration : IEntityTypeConfiguration<PaymentSaga>
{
    public void Configure(EntityTypeBuilder<PaymentSaga> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("payment_sagas");
        builder.HasKey(s => s.Id);

        // Concurrence optimiste via la colonne systeme xmin de PostgreSQL.
        // C'est elle qui empeche deux replicas du worker de faire avancer
        // la meme saga simultanement (donc de debiter deux fois).
        builder.Property(s => s.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.Property(s => s.State).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.InvoiceNumber).HasMaxLength(32).IsRequired();
        builder.Property(s => s.Currency).HasMaxLength(3).IsRequired();
        builder.Property(s => s.DebtorIban).HasMaxLength(34).IsRequired();
        builder.Property(s => s.PaymentReference).HasMaxLength(35).IsRequired();
        builder.Property(s => s.Amount).HasPrecision(18, 2);
        builder.Property(s => s.BankPaymentId).HasMaxLength(64);
        builder.Property(s => s.BankReference).HasMaxLength(64);
        builder.Property(s => s.FailureCode).HasMaxLength(50);
        builder.Property(s => s.FailureReason).HasMaxLength(1000);

        // Une seule saga par facture : c'est l'invariant qui empeche le double paiement
        // si InvoiceIssued est relivre apres expiration de la retention de l'inbox.
        builder.HasIndex(s => s.InvoiceId).IsUnique().HasDatabaseName("ux_payment_sagas_invoice");

        // Index de la requete du planificateur de reprise.
        builder.HasIndex(s => new { s.State, s.NextAttemptAt }).HasDatabaseName("ix_payment_sagas_due");

        builder.Ignore(s => s.DomainEvents);
        builder.Ignore(s => s.IdempotencyKey);
    }
}
