using Forterro.Invoicing.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forterro.Invoicing.Api.Infrastructure.Configurations;

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("invoices");
        builder.HasKey(i => i.Id);

        // Concurrence optimiste via xmin, la colonne systeme de PostgreSQL :
        // aucune colonne applicative a maintenir, aucun incrementeur a ne pas oublier.
        // Deux encaissements simultanes sur la meme facture -> le second leve
        // DbUpdateConcurrencyException au lieu d'ecraser le premier.
        //
        // Attention : IsRowVersion() seul (reflexe SQL Server) creerait une vraie
        // colonne "row_version" NOT NULL sans valeur par defaut, et tout INSERT echouerait.
        builder.Property(i => i.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.Property(i => i.Number).HasMaxLength(32);
        builder.HasIndex(i => i.Number).IsUnique().HasDatabaseName("ux_invoices_number");

        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.Currency).HasMaxLength(3).IsRequired();
        builder.Property(i => i.DebtorIban).HasMaxLength(34).IsRequired();
        builder.Property(i => i.CancellationReason).HasMaxLength(500);
        builder.Property(i => i.LastFailureCode).HasMaxLength(50);

        builder.ComplexProperty(i => i.Seller, p =>
        {
            p.Property(x => x.Name).HasColumnName("seller_name").HasMaxLength(200).IsRequired();
            p.Property(x => x.VatId).HasColumnName("seller_vat_id").HasMaxLength(20).IsRequired();
            p.Property(x => x.CountryCode).HasColumnName("seller_country").HasMaxLength(2).IsRequired();
            p.Property(x => x.AddressLine).HasColumnName("seller_address").HasMaxLength(200);
            p.Property(x => x.PostalCode).HasColumnName("seller_postal_code").HasMaxLength(20);
            p.Property(x => x.City).HasColumnName("seller_city").HasMaxLength(100);
        });

        builder.ComplexProperty(i => i.Buyer, p =>
        {
            p.Property(x => x.Name).HasColumnName("buyer_name").HasMaxLength(200).IsRequired();
            p.Property(x => x.VatId).HasColumnName("buyer_vat_id").HasMaxLength(20).IsRequired();
            p.Property(x => x.CountryCode).HasColumnName("buyer_country").HasMaxLength(2).IsRequired();
            p.Property(x => x.AddressLine).HasColumnName("buyer_address").HasMaxLength(200);
            p.Property(x => x.PostalCode).HasColumnName("buyer_postal_code").HasMaxLength(20);
            p.Property(x => x.City).HasColumnName("buyer_city").HasMaxLength(100);
        });

        // Les lignes appartiennent a la facture : owned collection, pas d'agregat separe.
        // Elles n'ont pas de cycle de vie propre et ne se referencent pas de l'exterieur.
        builder.OwnsMany(i => i.Lines, lines =>
        {
            lines.ToTable("invoice_lines");
            lines.WithOwner();
            lines.HasKey(l => l.Id);

            lines.Property(l => l.Description).HasMaxLength(500).IsRequired();
            lines.Property(l => l.Quantity).HasPrecision(18, 4);
            lines.Property(l => l.UnitPriceExclTax).HasPrecision(18, 4);
            lines.Property(l => l.VatRate).HasPrecision(5, 4);

            // Les montants de ligne sont calcules : ils ne sont pas persistes,
            // sinon ils peuvent diverger du prix unitaire apres une correction.
            lines.Ignore(l => l.AmountExclTax);
            lines.Ignore(l => l.TaxAmount);
            lines.Ignore(l => l.AmountInclTax);
        });

        // La collection est exposee en lecture seule : EF ecrit dans le champ, pas dans la propriete.
        builder.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Proprietes calculees : hors du modele relationnel.
        builder.Ignore(i => i.DomainEvents);
        builder.Ignore(i => i.TotalExclTax);
        builder.Ignore(i => i.TotalTax);
        builder.Ignore(i => i.TotalInclTax);
        builder.Ignore(i => i.PaymentReference);

        builder.HasIndex(i => new { i.Status, i.DueDate }).HasDatabaseName("ix_invoices_status_due");
    }
}

public sealed class InvoiceSequenceConfiguration : IEntityTypeConfiguration<InvoiceSequence>
{
    public void Configure(EntityTypeBuilder<InvoiceSequence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("invoice_sequences");
        builder.HasKey(s => new { s.SellerVatId, s.Year });
        builder.Property(s => s.SellerVatId).HasMaxLength(20);
    }
}
