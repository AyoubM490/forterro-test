using FluentValidation;
using Forterro.BuildingBlocks.Banking;

namespace Forterro.OpenBanking.Api.Endpoints;

public sealed class InitiatePaymentValidator : AbstractValidator<InitiatePaymentRequest>
{
    /// <summary>Plafond SEPA Credit Transfer standard. Au-dela il faut un virement de gros montant.</summary>
    private const decimal SepaMaxAmount = 999_999_999.99m;

    public InitiatePaymentValidator()
    {
        RuleFor(x => x.DebtorIban)
            .NotEmpty()
            .Must(Iban.IsValid).WithMessage("IBAN debiteur invalide.");

        RuleFor(x => x.CreditorIban)
            .NotEmpty()
            .Must(Iban.IsValid).WithMessage("IBAN beneficiaire invalide.");

        RuleFor(x => x)
            .Must(x => !string.Equals(Iban.Normalize(x.DebtorIban), Iban.Normalize(x.CreditorIban), StringComparison.Ordinal))
            .WithMessage("Le debiteur et le beneficiaire ne peuvent pas etre le meme compte.")
            .WithName("ibans");

        RuleFor(x => x.CreditorName).NotEmpty().MaximumLength(70);

        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .LessThanOrEqualTo(SepaMaxAmount);

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Equal("EUR", StringComparer.OrdinalIgnoreCase)
            .WithMessage("SEPA Credit Transfer n'accepte que l'euro.");

        RuleFor(x => x.EndToEndId)
            .NotEmpty()
            .MaximumLength(35)
            .WithMessage("L'identifiant de bout en bout est limite a 35 caracteres (norme SEPA).");

        RuleFor(x => x.RemittanceInformation)
            .MaximumLength(140)
            .WithMessage("Le libelle est limite a 140 caracteres (norme SEPA).");
    }
}
