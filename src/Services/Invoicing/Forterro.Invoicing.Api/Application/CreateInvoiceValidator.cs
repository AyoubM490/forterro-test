using FluentValidation;
using Forterro.BuildingBlocks.Banking;

namespace Forterro.Invoicing.Api.Application;

/// <summary>
/// Validation de forme, en amont du domaine.
/// Elle repond "cette requete est-elle bien formee ?", pas "cette operation est-elle permise ?" :
/// la seconde question appartient a l'agregat et n'est pas dupliquee ici.
/// </summary>
public sealed class CreateInvoiceValidator : AbstractValidator<CreateInvoiceRequest>
{
    public CreateInvoiceValidator()
    {
        RuleFor(x => x.Seller).NotNull().SetValidator(new PartyValidator());
        RuleFor(x => x.Buyer).NotNull().SetValidator(new PartyValidator());

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(3)
            .Matches("^[A-Za-z]{3}$")
            .WithMessage("La devise doit etre un code ISO 4217 (ex : EUR).");

        RuleFor(x => x.DebtorIban)
            .NotEmpty()
            .Must(Iban.IsValid)
            .WithMessage("IBAN invalide (cle de controle modulo 97 incorrecte).");

        RuleFor(x => x.DueDate)
            .GreaterThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("L'echeance ne peut pas etre dans le passe.");

        // Cascade Stop : sans elle, un corps sans "lines" passe le NotEmpty en echec mais
        // enchaine quand meme sur le Must, qui dereference une liste nulle. Le client recoit
        // alors 500 la ou la requete est simplement invalide.
        RuleFor(x => x.Lines)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Une facture doit comporter au moins une ligne.")
            .Must(l => l.Count <= 500).WithMessage("500 lignes maximum par facture.");

        RuleForEach(x => x.Lines).SetValidator(new InvoiceLineValidator());
    }
}

public sealed class PartyValidator : AbstractValidator<PartyDto>
{
    public PartyValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.CountryCode).NotEmpty().Length(2);
        RuleFor(x => x.VatId)
            .NotEmpty()
            .Matches("^[A-Za-z]{2}[A-Za-z0-9]{2,13}$")
            .WithMessage("Numero de TVA intracommunautaire mal forme (ex : FR12345678901).");
    }
}

public sealed class InvoiceLineValidator : AbstractValidator<InvoiceLineDto>
{
    public InvoiceLineValidator()
    {
        RuleFor(x => x.Description).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.UnitPriceExclTax).GreaterThanOrEqualTo(0);
        RuleFor(x => x.VatRate)
            .InclusiveBetween(0m, 1m)
            .WithMessage("Le taux de TVA s'exprime en fraction : 0.20 pour 20 %.");
    }
}

public sealed class CancelInvoiceValidator : AbstractValidator<CancelInvoiceRequest>
{
    public CancelInvoiceValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
