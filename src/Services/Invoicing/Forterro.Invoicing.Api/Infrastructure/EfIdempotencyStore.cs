using Forterro.BuildingBlocks.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forterro.Invoicing.Api.Infrastructure;

public sealed class IdempotencyRecord
{
    public required string Key { get; set; }

    public required int StatusCode { get; set; }

    public required string Body { get; set; }

    public required string RequestFingerprint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("idempotency_records");
        builder.HasKey(r => r.Key);
        builder.Property(r => r.Key).HasMaxLength(200);
        builder.Property(r => r.RequestFingerprint).HasMaxLength(64).IsRequired();
        builder.HasIndex(r => r.CreatedAt).HasDatabaseName("ix_idempotency_created_at");
    }
}

/// <summary>
/// Stockage des reponses idempotentes en base plutot qu'en cache distribue :
/// la garantie doit survivre a un redemarrage de Redis, sinon un rejeu apres incident
/// refait le POST. Sur de la facturation, on prefere payer un aller-retour SQL.
/// </summary>
public sealed class EfIdempotencyStore(InvoicingDbContext context) : IIdempotencyStore
{
    public async Task<IdempotentResponse?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var record = await context.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key, cancellationToken);

        return record is null
            ? null
            : new IdempotentResponse(record.StatusCode, record.Body, record.RequestFingerprint);
    }

    public async Task SaveAsync(string key, IdempotentResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        context.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Key = key,
            StatusCode = response.StatusCode,
            Body = response.Body,
            RequestFingerprint = response.RequestFingerprint,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Course entre deux requetes portant la meme cle : la contrainte d'unicite
            // a fait son travail, la premiere a gagne. Rien a corriger.
            context.ChangeTracker.Clear();
        }
    }
}
