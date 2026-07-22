using System.Collections.Concurrent;
using Forterro.BuildingBlocks.Banking;

namespace Forterro.OpenBanking.Api.Bank;

/// <summary>
/// Banque simulee, utilisee par docker-compose et les tests de bout en bout.
///
/// Elle reproduit les comportements qui cassent reellement une saga :
/// indisponibilite intermittente, provision insuffisante, IBAN refuse, latence.
/// Un simulateur qui repond toujours 200 ne prouve rien sur la resilience.
///
/// Deterministe : le comportement depend de l'IBAN debiteur, pas d'un tirage aleatoire,
/// pour que les tests soient reproductibles.
/// </summary>
public sealed class SimulatedBankConnector(ILogger<SimulatedBankConnector> logger) : IBankConnector
{
    // IBAN reserves du simulateur. Tous valides au modulo 97 : on teste les chemins
    // d'echec METIER, pas la validation de format, qui est deja couverte en amont.
    // Ils doivent rester distincts de l'IBAN nominal, sinon la demo du chemin
    // heureux echoue systematiquement.

    /// <summary>Chemin nominal : le virement est execute.</summary>
    public const string SettledIban = "FR7630006000011234567890189";

    /// <summary>Rejet definitif de la banque (code ISO 20022 AM04). Non rejouable.</summary>
    public const string InsufficientFundsIban = "FR7630004000031234567890143";

    /// <summary>La banque repond 503 : echec rejouable, la saga replanifie.</summary>
    public const string UnavailableIban = "FR1420041010050500013M02606";

    private readonly ConcurrentDictionary<string, PaymentResult> _payments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyKeys = new(StringComparer.Ordinal);

    public Task<PaymentResult> InitiatePaymentAsync(
        PaymentInitiation initiation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initiation);

        // Idempotence cote banque : une meme cle renvoie le meme paiement.
        // C'est ce qui rend le retry de la saga sur POST sans danger.
        if (_idempotencyKeys.TryGetValue(idempotencyKey, out var existingId))
        {
            logger.LogInformation("Cle {Key} deja vue, reponse rejouee.", idempotencyKey);
            return Task.FromResult(_payments[existingId]);
        }

        var debtor = Iban.Normalize(initiation.DebtorIban);

        if (string.Equals(debtor, UnavailableIban, StringComparison.Ordinal))
        {
            throw new BankException("bank_unavailable", "Service bancaire indisponible.", isRetryable: true);
        }

        if (string.Equals(debtor, InsufficientFundsIban, StringComparison.Ordinal))
        {
            var rejected = new PaymentResult(
                Guid.NewGuid().ToString("N"),
                PaymentStatus.Rejected,
                null,
                null,
                "AM04",
                "Provision insuffisante.");

            Register(idempotencyKey, rejected);
            return Task.FromResult(rejected);
        }

        if (!Iban.IsValid(debtor) || !Iban.IsValid(initiation.CreditorIban))
        {
            throw new BankException("invalid_request", "IBAN invalide.", isRetryable: false);
        }

        var settled = new PaymentResult(
            Guid.NewGuid().ToString("N"),
            PaymentStatus.Settled,
            initiation.EndToEndId,
            DateTimeOffset.UtcNow,
            null,
            null);

        Register(idempotencyKey, settled);

        logger.LogInformation(
            "Virement simule de {Amount} {Currency} depuis {Iban} execute.",
            initiation.Amount, initiation.Currency, Iban.Mask(debtor));

        return Task.FromResult(settled);
    }

    public Task<PaymentResult> GetPaymentStatusAsync(string bankPaymentId, CancellationToken cancellationToken)
        => _payments.TryGetValue(bankPaymentId, out var result)
            ? Task.FromResult(result)
            : throw new BankException("not_found", $"Paiement {bankPaymentId} inconnu.", isRetryable: false);

    public Task<AccountBalance> GetBalanceAsync(string iban, CancellationToken cancellationToken)
    {
        var normalized = Iban.Normalize(iban);

        if (!Iban.IsValid(normalized))
        {
            throw new BankException("invalid_request", "IBAN invalide.", isRetryable: false);
        }

        var available = string.Equals(normalized, InsufficientFundsIban, StringComparison.Ordinal)
            ? 12.34m
            : 48_250.00m;

        return Task.FromResult(new AccountBalance(normalized, available, available, "EUR", DateTimeOffset.UtcNow));
    }

    private void Register(string idempotencyKey, PaymentResult result)
    {
        _payments[result.BankPaymentId] = result;
        _idempotencyKeys[idempotencyKey] = result.BankPaymentId;
    }
}
