using System;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Coflnet.Sky.SkyAuctionTracker.Infrastructure;

internal static class DatabaseReadiness
{
    public static async Task CheckAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await Task.Run(async () =>
            {
                var session = services.GetRequiredService<ISession>();
                using var scope = services.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
                await Task.WhenAll(
                    session.ExecuteAsync(new SimpleStatement("SELECT key FROM system.local").SetReadTimeoutMillis(3000)),
                    database.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token));
            }, timeout.Token).WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
        }
        finally { timeout.Cancel(); }
    }
}
