using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Forterro.Bff.Tests;

/// <summary>
/// L'agregation doit rester utile quand une moitie manque. C'est la raison d'etre du BFF :
/// contenir la panne d'un service secondaire au lieu de la propager a tout l'ecran.
/// </summary>
public sealed class OverviewEndpointTests : IDisposable
{
    private static readonly Guid InvoiceId = Guid.Parse("6f1c0d5e-6a2c-4f2e-9f77-2f3b7d5c1a10");

    private readonly BffFactory _factory = new();

    [Fact]
    public async Task Compose_la_facture_et_le_paiement_en_un_seul_appel()
    {
        _factory.Invoicing.Body = """{"id":"6f1c0d5e-6a2c-4f2e-9f77-2f3b7d5c1a10","status":"issued"}""";
        _factory.Payments.Body = """{"state":"awaitingBank","attempts":1}""";

        var overview = await GetOverviewAsync("invoicing:read payments:read");

        overview.GetProperty("paymentAvailability").GetString().Should().Be("available");
        overview.GetProperty("status").GetString()
            .Should().Be("Ordre transmis a la banque, en attente de confirmation.");

        _factory.Invoicing.CallCount.Should().Be(1);
        _factory.Payments.CallCount.Should().Be(1, "les deux appels partent en parallele, une seule fois chacun");
    }

    [Fact]
    public async Task Degrade_sans_echouer_quand_le_scope_de_paiement_manque()
    {
        // Le cas de erp-product-line : elle a les scopes de facturation, pas ceux de paiement.
        // L'ecran doit afficher la facture et dire pourquoi le bloc paiement est vide.
        _factory.Invoicing.Body = """{"id":"6f1c0d5e-6a2c-4f2e-9f77-2f3b7d5c1a10","status":"issued"}""";
        _factory.Payments.StatusCode = HttpStatusCode.Forbidden;

        var overview = await GetOverviewAsync("invoicing:read");

        overview.GetProperty("paymentAvailability").GetString().Should().Be("forbidden");
        overview.GetProperty("status").GetString()
            .Should().Be("Avancement du paiement non visible avec les droits actuels.");
        overview.TryGetProperty("payment", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Degrade_sans_echouer_quand_le_service_de_paiement_est_en_panne()
    {
        _factory.Invoicing.Body = """{"id":"6f1c0d5e-6a2c-4f2e-9f77-2f3b7d5c1a10","status":"issued"}""";
        _factory.Payments.StatusCode = HttpStatusCode.InternalServerError;

        var overview = await GetOverviewAsync("invoicing:read payments:read");

        overview.GetProperty("paymentAvailability").GetString().Should().Be("unavailable");
        overview.GetProperty("status").GetString()
            .Should().Be("Avancement du paiement temporairement indisponible.");
    }

    [Fact]
    public async Task Signale_le_cas_ou_un_humain_doit_intervenir()
    {
        // Virement parti puis facture annulee : le seul etat ou l'ecran ne doit surtout pas
        // proposer de reessayer. La regle vit ici et pas dans chaque frontal.
        _factory.Invoicing.Body = """{"id":"6f1c0d5e-6a2c-4f2e-9f77-2f3b7d5c1a10","status":"cancelled"}""";
        _factory.Payments.Body = """{"state":"failed","failureCode":"compensation_required"}""";

        var overview = await GetOverviewAsync("invoicing:read payments:read");

        overview.GetProperty("status").GetString()
            .Should().Be("Virement parti puis facture annulee : intervention manuelle requise.");
    }

    [Fact]
    public async Task Renvoie_404_quand_la_facture_elle_meme_est_absente()
    {
        // La facture est la ressource principale : sans elle il n'y a rien a composer.
        _factory.Invoicing.StatusCode = HttpStatusCode.NotFound;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, "invoicing:read");

        using var response = await client.GetAsync(new Uri($"/bff/invoices/{InvoiceId}/overview", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refuse_un_appelant_sans_session_ni_jeton()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/bff/invoices/{InvoiceId}/overview", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Invoicing.CallCount.Should().Be(0, "rien ne doit partir en aval sans authentification");
    }

    private async Task<JsonElement> GetOverviewAsync(string scopes)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, scopes);

        using var response = await client.GetAsync(new Uri($"/bff/invoices/{InvoiceId}/overview", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public void Dispose() => _factory.Dispose();
}
