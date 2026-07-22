using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Forterro.BuildingBlocks.Api;
using Forterro.Contracts;
using Forterro.Invoicing.Api.Application;
using Forterro.Invoicing.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Forterro.Invoicing.Tests.Integration;

[Collection(nameof(InvoicingCollection))]
public class InvoiceApiTests(InvoicingApiFactory factory)
{
    private const string ValidIban = "FR7630006000011234567890189";

    [Fact]
    public async Task Un_appel_sans_jeton_est_refuse()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/invoices", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Un_scope_de_lecture_ne_permet_pas_d_ecrire()
    {
        using var client = CreateClient("invoicing:read");

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/invoices", UriKind.Relative), BuildRequest());

        // 403 et non 401 : le client est authentifie, il lui manque le scope.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Creation_puis_emission_d_une_facture()
    {
        using var client = CreateClient("invoicing:write");

        using var createResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/invoices", UriKind.Relative), BuildRequest());

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var draft = await createResponse.Content.ReadFromJsonAsync<InvoiceResponse>(ApiJson.Options);
        draft!.Status.Should().Be(InvoiceStatus.Draft);
        draft.TotalInclTax.Should().Be(1200m);

        // L'IBAN ne doit jamais ressortir en clair d'une API.
        draft.DebtorIban.Should().NotBe(ValidIban).And.Contain("*");

        using var issueResponse = await client.PostAsync(
            new Uri($"/api/v1/invoices/{draft.Id}/issue", UriKind.Relative), content: null);

        issueResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var issued = await issueResponse.Content.ReadFromJsonAsync<InvoiceResponse>(ApiJson.Options);
        issued!.Status.Should().Be(InvoiceStatus.Issued);
        issued.Number.Should().MatchRegex(@"^INV-\d{4}-\d{6}$");
    }

    /// <summary>
    /// Le test qui justifie tout le pattern Outbox : l'evenement doit se trouver
    /// dans la MEME base, ecrit par la MEME transaction que le changement d'etat.
    /// </summary>
    [Fact]
    public async Task L_emission_ecrit_l_evenement_dans_l_outbox_de_facon_transactionnelle()
    {
        using var client = CreateClient("invoicing:write");

        var draft = await CreateDraftAsync(client);
        await client.PostAsync(new Uri($"/api/v1/invoices/{draft.Id}/issue", UriKind.Relative), null);

        await using var db = factory.CreateDbContext();

        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == draft.Id);
        var outboxMessage = await db.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(m => m.PartitionKey == draft.Id.ToString());

        invoice.Status.Should().Be(InvoiceStatus.Issued);
        outboxMessage.Should().NotBeNull();
        outboxMessage!.ContractName.Should().Be(ContractNames.InvoiceIssued);
        outboxMessage.Topic.Should().Be(Topics.Invoicing);
        outboxMessage.ProcessedAt.Should().BeNull("le dispatcher est desactive dans ce test");
        outboxMessage.Payload.Should().Contain(invoice.Number);
    }

    [Fact]
    public async Task Une_facture_emise_deux_fois_renvoie_409()
    {
        using var client = CreateClient("invoicing:write");

        var draft = await CreateDraftAsync(client);
        await client.PostAsync(new Uri($"/api/v1/invoices/{draft.Id}/issue", UriKind.Relative), null);

        using var second = await client.PostAsync(
            new Uri($"/api/v1/invoices/{draft.Id}/issue", UriKind.Relative), null);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task La_cle_d_idempotence_rejoue_la_premiere_reponse()
    {
        using var client = CreateClient("invoicing:write");
        var key = Guid.NewGuid().ToString();
        var request = BuildRequest();

        using var first = await PostWithKeyAsync(client, request, key);
        using var second = await PostWithKeyAsync(client, request, key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.Contains("Idempotency-Replayed").Should().BeTrue();

        var firstBody = await first.Content.ReadFromJsonAsync<InvoiceResponse>(ApiJson.Options);
        var secondBody = await second.Content.ReadFromJsonAsync<InvoiceResponse>(ApiJson.Options);

        // Meme identifiant : une seule facture a ete creee, pas deux.
        secondBody!.Id.Should().Be(firstBody!.Id);

        await using var db = factory.CreateDbContext();
        var count = await db.Invoices.CountAsync(i => i.Id == firstBody.Id);
        count.Should().Be(1);
    }

    [Fact]
    public async Task Reutiliser_une_cle_avec_un_corps_different_est_refuse()
    {
        using var client = CreateClient("invoicing:write");
        var key = Guid.NewGuid().ToString();

        using var first = await PostWithKeyAsync(client, BuildRequest(), key);
        using var second = await PostWithKeyAsync(client, BuildRequest(amount: 999m), key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Un_iban_invalide_renvoie_une_erreur_de_validation()
    {
        using var client = CreateClient("invoicing:write");

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/invoices", UriKind.Relative),
            BuildRequest() with { DebtorIban = "FR7630006000011234567890188" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("IBAN");
    }

    [Fact]
    public async Task Les_numeros_de_facture_se_suivent_sans_trou()
    {
        using var client = CreateClient("invoicing:write");

        var numbers = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var draft = await CreateDraftAsync(client);
            using var response = await client.PostAsync(
                new Uri($"/api/v1/invoices/{draft.Id}/issue", UriKind.Relative), null);

            var issued = await response.Content.ReadFromJsonAsync<InvoiceResponse>(ApiJson.Options);
            numbers.Add(issued!.Number!);
        }

        var sequence = numbers
            .Select(n => int.Parse(n.Split('-')[2], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        // Sequence strictement continue : c'est une exigence legale, pas un confort.
        sequence.Should().BeInAscendingOrder();
        (sequence[^1] - sequence[0]).Should().Be(sequence.Count - 1);
    }

    [Fact]
    public async Task La_pagination_par_curseur_ne_renvoie_pas_deux_fois_le_meme_element()
    {
        using var client = CreateClient("invoicing:write");

        for (var i = 0; i < 5; i++)
        {
            await CreateDraftAsync(client);
        }

        using var firstPage = await client.GetAsync(
            new Uri("/api/v1/invoices?pageSize=2", UriKind.Relative));
        var page1 = await firstPage.Content.ReadFromJsonAsync<PagedResult<InvoiceResponse>>(ApiJson.Options);

        page1!.Items.Should().HaveCount(2);
        page1.NextCursor.Should().NotBeNull();

        using var secondPage = await client.GetAsync(
            new Uri($"/api/v1/invoices?pageSize=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}", UriKind.Relative));
        var page2 = await secondPage.Content.ReadFromJsonAsync<PagedResult<InvoiceResponse>>(ApiJson.Options);

        page2!.Items.Select(i => i.Id).Should().NotIntersectWith(page1.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Un_curseur_corrompu_ne_provoque_pas_d_erreur_500()
    {
        using var client = CreateClient("invoicing:read");

        using var response = await client.GetAsync(
            new Uri("/api/v1/invoices?cursor=ceci-nest-pas-du-base64", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Une_facture_inexistante_renvoie_un_problem_details()
    {
        using var client = CreateClient("invoicing:read");

        using var response = await client.GetAsync(
            new Uri($"/api/v1/invoices/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("traceId", "le support doit pouvoir remonter a la trace");
        body.Should().Contain("resource_not_found");
    }

    // --- Utilitaires ------------------------------------------------------

    private HttpClient CreateClient(string scopes)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, scopes);
        return client;
    }

    private static async Task<InvoiceResponse> CreateDraftAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/invoices", UriKind.Relative), BuildRequest());

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvoiceResponse>(ApiJson.Options))!;
    }

    private static async Task<HttpResponseMessage> PostWithKeyAsync(
        HttpClient client,
        CreateInvoiceRequest request,
        string idempotencyKey)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invoices")
        {
            Content = JsonContent.Create(request),
        };

        message.Headers.Add("Idempotency-Key", idempotencyKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await client.SendAsync(message);
    }

    private static CreateInvoiceRequest BuildRequest(decimal amount = 500m) => new(
        new PartyDto("Forterro France", "FR12345678901", "FR", City: "Lyon"),
        new PartyDto("Manufacture Dupont", "FR98765432109", "FR", City: "Grenoble"),
        "EUR",
        ValidIban,
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
        [new InvoiceLineDto("Licence ERP", 2, amount, 0.20m)]);
}

[CollectionDefinition(nameof(InvoicingCollection))]
public sealed class InvoicingCollection : ICollectionFixture<InvoicingApiFactory>;
