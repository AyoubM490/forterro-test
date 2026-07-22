using System.Net;
using FluentAssertions;
using Xunit;

namespace Forterro.Bff.Tests;

public sealed class RateLimitingTests : IDisposable
{
    private readonly BffFactory _factory = new() { SessionPermitLimit = 3 };

    [Fact]
    public async Task Refuse_au_dela_du_quota_et_indique_quand_reessayer()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, "invoicing:read");

        for (var i = 0; i < 3; i++)
        {
            using var allowed = await client.GetAsync(new Uri("/bff/me", UriKind.Relative));
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, "les {0} premieres requetes sont dans le quota", 3);
        }

        using var rejected = await client.GetAsync(new Uri("/bff/me", UriKind.Relative));

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Sans Retry-After, un client bien ecrit n'a aucun moyen de savoir quand reprendre
        // et boucle immediatement, ce qui aggrave la charge au pire moment.
        rejected.Headers.Should().ContainSingle(h => h.Key == "Retry-After");
    }

    public void Dispose() => _factory.Dispose();
}
