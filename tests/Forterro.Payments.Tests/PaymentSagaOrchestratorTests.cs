using FluentAssertions;
using Forterro.BuildingBlocks.Messaging;
using Forterro.BuildingBlocks.Outbox;
using Forterro.Contracts;
using Forterro.Payments.Worker.Application;
using Forterro.Payments.Worker.Domain;
using Forterro.Payments.Worker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Forterro.Payments.Tests;

/// <summary>
/// Tests de l'orchestrateur sur un vrai PostgreSQL : ce qu'on verifie ici,
/// c'est la persistance de l'etat entre les tentatives et l'ecriture des evenements
/// dans l'Outbox. Un double en memoire ne prouverait ni l'un ni l'autre.
/// La banque, elle, est bien un double : on veut controler ses reponses.
/// </summary>
public sealed class PaymentSagaOrchestratorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("forterro_payments_test")
        .WithUsername("forterro")
        .WithPassword("forterro")
        .Build();

    private PaymentsDbContext _context = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _context = new PaymentsDbContext(
            new DbContextOptionsBuilder<PaymentsDbContext>()
                .UseNpgsql(_postgres.GetConnectionString())
                .Options);

        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Un_paiement_reussi_regle_la_saga_et_ecrit_PaymentSettled_dans_l_outbox()
    {
        var bank = Substitute.For<IOpenBankingClient>();
        bank.InitiateAsync(default!, default!, default!, default, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(new PaymentOutcome(
                "bank-1", BankPaymentStatus.Settled, "E2E-1", DateTimeOffset.UtcNow, null, null));

        var orchestrator = BuildOrchestrator(bank);
        var issued = BuildInvoiceIssued();

        await orchestrator.StartAsync(issued, CancellationToken.None);

        var saga = await _context.Sagas.AsNoTracking().SingleAsync(s => s.InvoiceId == issued.InvoiceId);
        saga.State.Should().Be(SagaState.Settled);
        saga.BankReference.Should().Be("E2E-1");

        var outbox = await _context.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.ContractName == ContractNames.PaymentSettled);

        outbox.Topic.Should().Be(Topics.Payments);
        outbox.PartitionKey.Should().Be(issued.InvoiceId.ToString());
    }

    [Fact]
    public async Task Une_banque_indisponible_planifie_une_reprise_sans_publier_d_echec()
    {
        var bank = Substitute.For<IOpenBankingClient>();
        bank.InitiateAsync(default!, default!, default!, default, default!, default!, default, default!, default)
            .ThrowsAsyncForAnyArgs(new OpenBankingCallException("bank_unavailable", "503", isRetryable: true));

        var orchestrator = BuildOrchestrator(bank);
        var issued = BuildInvoiceIssued();

        await orchestrator.StartAsync(issued, CancellationToken.None);

        var saga = await _context.Sagas.AsNoTracking().SingleAsync(s => s.InvoiceId == issued.InvoiceId);
        saga.State.Should().Be(SagaState.AwaitingRetry);
        saga.NextAttemptAt.Should().NotBeNull().And.BeAfter(DateTimeOffset.UtcNow);

        // Rien ne doit partir en aval tant que la reprise est possible :
        // publier PaymentFailed ici ferait basculer la facture en relance a tort.
        var outbox = await _context.OutboxMessages.AsNoTracking()
            .Where(m => m.PartitionKey == issued.InvoiceId.ToString())
            .ToListAsync();

        outbox.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_rejet_pour_provision_insuffisante_publie_PaymentFailed_non_rejouable()
    {
        var bank = Substitute.For<IOpenBankingClient>();
        bank.InitiateAsync(default!, default!, default!, default, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(new PaymentOutcome(
                "bank-2", BankPaymentStatus.Rejected, null, null, "AM04", "Provision insuffisante."));

        var orchestrator = BuildOrchestrator(bank);
        var issued = BuildInvoiceIssued();

        await orchestrator.StartAsync(issued, CancellationToken.None);

        var saga = await _context.Sagas.AsNoTracking().SingleAsync(s => s.InvoiceId == issued.InvoiceId);
        saga.State.Should().Be(SagaState.Failed);
        saga.FailureCode.Should().Be(PaymentFailureCodes.InsufficientFunds);

        var outbox = await _context.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.ContractName == ContractNames.PaymentFailed);

        outbox.Payload.Should().Contain(PaymentFailureCodes.InsufficientFunds);
    }

    /// <summary>
    /// Protection contre la relivraison Kafka : le meme InvoiceIssued recu deux fois
    /// ne doit produire qu'une saga, donc qu'un seul debit.
    /// </summary>
    [Fact]
    public async Task Le_meme_InvoiceIssued_recu_deux_fois_ne_cree_qu_une_saga()
    {
        var bank = Substitute.For<IOpenBankingClient>();
        bank.InitiateAsync(default!, default!, default!, default, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(new PaymentOutcome(
                "bank-3", BankPaymentStatus.Settled, "E2E-3", DateTimeOffset.UtcNow, null, null));

        var issued = BuildInvoiceIssued();

        await BuildOrchestrator(bank).StartAsync(issued, CancellationToken.None);
        _context.ChangeTracker.Clear();
        await BuildOrchestrator(bank).StartAsync(issued, CancellationToken.None);

        var sagas = await _context.Sagas.AsNoTracking()
            .Where(s => s.InvoiceId == issued.InvoiceId).ToListAsync();

        sagas.Should().ContainSingle();

        await bank.ReceivedWithAnyArgs(1).InitiateAsync(
            default!, default!, default!, default, default!, default!, default, default!, default);
    }

    private PaymentSagaOrchestrator BuildOrchestrator(IOpenBankingClient bank)
    {
        var registry = new IntegrationEventRegistry().AddBusinessServicesContracts();

        return new PaymentSagaOrchestrator(
            _context,
            bank,
            new OutboxWriter<PaymentsDbContext>(_context, registry),
            Options.Create(new SagaOptions
            {
                MaxAttempts = 3,
                BaseRetryDelay = TimeSpan.FromSeconds(30),
                CreditorIban = "FR7630001007941234567890185",
                CreditorName = "Forterro Group",
            }),
            NullLogger<PaymentSagaOrchestrator>.Instance);
    }

    private static InvoiceIssued BuildInvoiceIssued() => new()
    {
        InvoiceId = Guid.NewGuid(),
        InvoiceNumber = "INV-2026-000042",
        SellerVatId = "FR12345678901",
        BuyerVatId = "FR98765432109",
        DebtorIban = "FR7630006000011234567890189",
        TotalInclTax = 1200m,
        Currency = "EUR",
        DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        PaymentReference = "RF1234567890ABCDEF",
    };
}
