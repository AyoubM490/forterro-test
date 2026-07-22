using Microsoft.EntityFrameworkCore;

namespace Forterro.Invoicing.Api.Infrastructure;

public static class DatabaseMigrationExtensions
{
    /// <summary>
    /// Migration au demarrage : pratique en developpement et pour la demo docker-compose.
    ///
    /// En production on ne fait PAS ca : avec N replicas, N pods tentent de migrer en meme temps,
    /// et un rollback applicatif laisse un schema deja migre. Les migrations passent par un
    /// Job Kubernetes dedie, execute avant le rollout (cf. deploy/k8s/migration-job.yaml).
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoicingDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<InvoicingDbContext>>();

        logger.LogInformation("Application des migrations en attente...");
        await context.Database.MigrateAsync();
        logger.LogInformation("Schema a jour.");
    }
}
