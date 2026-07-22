using FluentAssertions;
using Forterro.Bff.Authentication;
using Forterro.Bff.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Forterro.Bff.Tests;

public sealed class SchemeSelectionTests
{
    [Theory]
    [InlineData("Bearer eyJhbGciOi...", true)]
    [InlineData("bearer eyJhbGciOi...", false)]  // casse differente : ce n'est pas le schema attendu
    [InlineData("Basic dXNlcjpwYXNz", false)]
    [InlineData("", false)]
    public void Reconnait_un_appel_machine_a_son_en_tete(string authorization, bool expected)
    {
        var request = new DefaultHttpContext().Request;

        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.Authorization = authorization;
        }

        BffAuthentication.IsMachineRequest(request).Should().Be(expected);
    }

    [Fact]
    public void Un_cookie_seul_designe_un_navigateur()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "forterro.session=abc";

        BffAuthentication.IsMachineRequest(context.Request).Should().BeFalse();
    }
}

/// <summary>
/// La redirection ouverte est la faille classique d'un parametre de retour : sans filtre,
/// le BFF authentifie l'utilisateur puis le depose sur un site controle par l'attaquant,
/// qui affiche une fausse page de login.
/// </summary>
public sealed class ReturnUrlTests
{
    private static readonly BffOptions Options = new() { DefaultReturnPath = "/" };

    [Theory]
    [InlineData("/factures", "/factures")]
    [InlineData("/factures?statut=paid", "/factures?statut=paid")]
    public void Accepte_un_chemin_local(string candidate, string expected)
        => SessionEndpoints.SafeReturnUrl(candidate, Options).Should().Be(expected);

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]           // URL protocol-relative : le navigateur y voit un hote
    [InlineData("http://localhost:5000/ok")] // meme un hote legitime doit passer par un chemin
    [InlineData("factures")]                 // sans slash initial, la resolution est ambigue
    [InlineData(null)]
    [InlineData("")]
    public void Refuse_tout_le_reste(string? candidate)
        => SessionEndpoints.SafeReturnUrl(candidate, Options).Should().Be("/");
}
