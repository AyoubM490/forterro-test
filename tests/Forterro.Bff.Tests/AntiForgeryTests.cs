using Forterro.Bff.Authentication;
using Forterro.Bff.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Forterro.Bff.Tests;

/// <summary>
/// Le garde anti-CSRF est la piece dont une erreur ne se voit pas : trop laxiste, il laisse
/// passer des requetes forgees sans que rien n'echoue ; trop strict, il casse les appels
/// machine. Chaque cas est donc verrouille explicitement.
/// </summary>
public sealed class AntiForgeryTests
{
    private static readonly IOptions<BffOptions> Options = Microsoft.Extensions.Options.Options.Create(new BffOptions());

    [Fact]
    public async Task Rejette_une_ecriture_par_cookie_sans_en_tete()
    {
        var context = CreateContext("POST");

        var reached = await InvokeAsync(context);

        reached.Should().BeFalse("la requete ne doit jamais atteindre l'aval");
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Accepte_une_ecriture_par_cookie_avec_en_tete()
    {
        var context = CreateContext("POST");
        context.Request.Headers[AntiForgeryMiddleware.HeaderName] = "1";

        var reached = await InvokeAsync(context);

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task N_exige_rien_d_un_appel_machine()
    {
        // Un jeton porteur n'est pas ambiant : l'appelant doit le poser lui-meme, donc une
        // page tierce ne peut pas le declencher. Exiger en plus un en-tete anti-CSRF d'un
        // worker serait de la ceremonie sans gain de securite.
        var context = CreateContext("POST");
        context.Request.Headers.Authorization = "Bearer un-jeton";

        var reached = await InvokeAsync(context);

        reached.Should().BeTrue();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Laisse_passer_les_methodes_sans_effet(string method)
    {
        var context = CreateContext(method);

        var reached = await InvokeAsync(context);

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Rejette_une_origine_tierce_meme_avec_l_en_tete()
    {
        var context = CreateContext("POST");
        context.Request.Headers[AntiForgeryMiddleware.HeaderName] = "1";
        context.Request.Headers.Origin = "https://evil.example";

        var reached = await InvokeAsync(context);

        reached.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Accepte_l_origine_du_bff_lui_meme()
    {
        var context = CreateContext("POST");
        context.Request.Headers[AntiForgeryMiddleware.HeaderName] = "1";
        context.Request.Headers.Origin = "https://api.forterro.example";

        var reached = await InvokeAsync(context);

        reached.Should().BeTrue();
    }

    private static DefaultHttpContext CreateContext(string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.forterro.example");
        context.Response.Body = new MemoryStream();

        return context;
    }

    private static async Task<bool> InvokeAsync(HttpContext context)
    {
        var reached = false;

        var middleware = new AntiForgeryMiddleware(
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            },
            Options);

        await middleware.InvokeAsync(context);

        return reached;
    }
}
