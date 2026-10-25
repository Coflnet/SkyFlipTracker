using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>Private listener: business metrics never enter the public application registry.</summary>
public sealed class ProfitMetricExporter(ProfitMetricProjection projection, IConfiguration configuration, ILogger<ProfitMetricExporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!projection.Enabled) return;
        var port = configuration.GetValue("ProfitMetrics:Port", 9092);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();
        using var registration = stoppingToken.Register(listener.Stop);
        var lookback = Math.Clamp(configuration.GetValue("ProfitMetrics:LookbackDays", 8), 2, 8);
        ProfitMetricProjection.Snapshot[] cached = null;
        var computedAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (stoppingToken.IsCancellationRequested) { return; }
            try
            {
                if (context.Request.HttpMethod != "GET" || context.Request.Url.AbsolutePath != "/metrics")
                {
                    context.Response.StatusCode = 404;
                    continue;
                }
                var currentDay = DateTime.UtcNow.Date;
                if (cached == null || cached[0].Day != currentDay || computedAt < DateTimeOffset.UtcNow.AddMinutes(-5))
                {
                    var refreshed = new ProfitMetricProjection.Snapshot[lookback];
                    for (var offset = 0; offset < lookback; offset++)
                        refreshed[offset] = await projection.Read(currentDay.AddDays(-offset), stoppingToken);
                    cached = refreshed;
                    computedAt = DateTimeOffset.UtcNow;
                }
                var text = "# TYPE sky_fliptracker_profit_complete gauge\n# TYPE sky_fliptracker_realized_profit_coins gauge\n# TYPE sky_fliptracker_profit_snapshot_timestamp_seconds gauge\n";
                foreach (var snapshot in cached)
                {
                    var day = snapshot.Day.ToString("yyyy-MM-dd");
                    text += $"sky_fliptracker_profit_complete{{day=\"{day}\"}} {(snapshot.Complete ? 1 : 0)}\n";
                    if (snapshot.Complete)
                        text += $"sky_fliptracker_realized_profit_coins{{day=\"{day}\"}} {snapshot.Coins}\n"
                            + $"sky_fliptracker_profit_snapshot_timestamp_seconds{{day=\"{day}\"}} {computedAt.ToUnixTimeSeconds()}\n";
                }
                text += $"# TYPE sky_fliptracker_profit_coverage_start_timestamp_seconds gauge\nsky_fliptracker_profit_coverage_start_timestamp_seconds {new DateTimeOffset(projection.CoverageStart).ToUnixTimeSeconds()}\n";
                var payload = Encoding.UTF8.GetBytes(text);
                context.Response.ContentType = "text/plain; version=0.0.4";
                await context.Response.OutputStream.WriteAsync(payload, stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Private profit metrics are unavailable ({Type})", exception.GetType().Name);
                context.Response.StatusCode = 503;
            }
            finally { context.Response.Close(); }
        }
    }
}
