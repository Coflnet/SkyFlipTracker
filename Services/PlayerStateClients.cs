using System;
using Coflnet.Sky.PlayerState.Client.Api;
using Coflnet.Sky.PlayerState.Client.Client;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Creates the player-state clients with a bounded timeout. The generated clients wait 100 seconds by default,
/// a slow player-state then held a consumed batch for longer than the kafka poll interval and got the pods evicted from the group.
/// A timeout surfaces as an exception that <see cref="TrackerBackgroundService.IsInfrastructureError"/> retries, the lookups
/// decide the acquisition cost of a flip so they are never skipped.
/// </summary>
public static class PlayerStateClients
{
    /// <summary>Seconds to wait for one player-state request, <c>PLAYERSTATE_TIMEOUT_SECONDS</c> overrides</summary>
    public const int DefaultTimeoutSeconds = 30;

    public static TimeSpan GetTimeout(IConfiguration config)
    {
        if (int.TryParse(config?["PLAYERSTATE_TIMEOUT_SECONDS"], out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    public static Configuration CreateConfiguration(IConfiguration config)
    {
        return new Configuration
        {
            BasePath = config["PLAYERSTATE_BASE_URL"],
            Timeout = GetTimeout(config)
        };
    }

    public static ITransactionApi CreateTransactionApi(IConfiguration config) => new TransactionApi(CreateConfiguration(config));

    public static IItemsApi CreateItemsApi(IConfiguration config) => new ItemsApi(CreateConfiguration(config));
}
