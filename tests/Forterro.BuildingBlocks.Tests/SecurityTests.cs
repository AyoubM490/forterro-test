using System.Security.Claims;
using FluentAssertions;
using Forterro.BuildingBlocks.Persistence;
using Forterro.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Forterro.BuildingBlocks.Tests;

public class ScopeAuthorizationTests
{
    [Fact]
    public async Task Autorise_quand_un_scope_requis_est_present()
    {
        var handler = new ScopeAuthorizationHandler();
        var requirement = new ScopeRequirement("invoicing:write");
        var user = BuildUser(("scope", "openid profile invoicing:read invoicing:write"));

        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Refuse_quand_aucun_scope_requis_n_est_present()
    {
        var handler = new ScopeAuthorizationHandler();
        var requirement = new ScopeRequirement("invoicing:write");
        var user = BuildUser(("scope", "openid profile invoicing:read"));

        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public void Extrait_les_scopes_de_la_claim_scp_azure()
    {
        var user = BuildUser(("scp", "payments:read payments:write"));

        ScopeAuthorizationHandler.ExtractScopes(user)
            .Should().BeEquivalentTo(["payments:read", "payments:write"]);
    }

    [Fact]
    public void Un_scope_ne_matche_pas_par_prefixe()
    {
        // "invoicing:read" ne doit jamais satisfaire une exigence "invoicing:read-all".
        var user = BuildUser(("scope", "invoicing:read"));

        ScopeAuthorizationHandler.ExtractScopes(user).Should().NotContain("invoicing:read-all");
    }

    private static ClaimsPrincipal BuildUser(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity([.. claims.Select(c => new Claim(c.Type, c.Value))], "Test"));
}

public class KeycloakClaimsFlattenerTests
{
    [Fact]
    public void Remonte_les_roles_de_realm_access()
    {
        var identity = new ClaimsIdentity([
            new Claim(KeycloakClaimsFlattener.RealmAccessClaim, """{"roles":["billing-admin","viewer"]}"""),
        ]);

        KeycloakClaimsFlattener.Flatten(identity);

        identity.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(["billing-admin", "viewer"]);
    }

    [Fact]
    public void Remonte_les_roles_de_chaque_client_dans_resource_access()
    {
        var identity = new ClaimsIdentity([
            new Claim(
                KeycloakClaimsFlattener.ResourceAccessClaim,
                """{"invoicing-api":{"roles":["issuer"]},"payments-api":{"roles":["operator"]}}"""),
        ]);

        KeycloakClaimsFlattener.Flatten(identity);

        identity.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(["issuer", "operator"]);
    }

    [Fact]
    public void Un_json_malforme_ne_fait_pas_tomber_l_authentification()
    {
        var identity = new ClaimsIdentity([
            new Claim(KeycloakClaimsFlattener.RealmAccessClaim, "{ceci n'est pas du json"),
        ]);

        var act = () => KeycloakClaimsFlattener.Flatten(identity);

        // Le principal sort simplement sans role : il sera refuse par les policies,
        // ce qui est le comportement sur : fail closed.
        act.Should().NotThrow();
        identity.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }
}

public class SnakeCaseTests
{
    [Theory]
    [InlineData("Id", "id")]
    [InlineData("InvoiceId", "invoice_id")]
    [InlineData("TotalInclTax", "total_incl_tax")]
    [InlineData("VatId", "vat_id")]
    [InlineData("OutboxMessages", "outbox_messages")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("HTTPResponse", "http_response")]
    [InlineData("IBANCode", "iban_code")]
    public void Convertit_correctement(string input, string expected)
        => SnakeCaseNamingExtensions.ToSnakeCase(input).Should().Be(expected);
}
