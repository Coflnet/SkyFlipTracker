using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Export and erasure of the flips of a single player.
/// Outsped flips are not included, they are keyed by item tag and only reference the purchase auction, not the player.
/// </summary>
public class PrivacyService
{
    private readonly FlipStorageService storage;
    private readonly ILogger<PrivacyService> logger;
    private readonly ConcurrentDictionary<string, bool> cleaned = new();
    private readonly SemaphoreSlim cleanupLock = new(1, 1);

    public PrivacyService(FlipStorageService storage, ILogger<PrivacyService> logger = null)
    {
        this.storage = storage;
        this.logger = logger;
    }

    /// <summary>
    /// Deletes the stored flips of all currently opted out players. Players already handled by this process are skipped
    /// to avoid writing a partition tombstone for every uuid on every refresh; failed ones are retried on the next call.
    /// unknown_flips2 is keyed by finder and expires via its 14 day ttl, so it is not touched.
    /// </summary>
    public Task RemoveOptedOut(CancellationToken token = default) => RemoveOptedOut(PlayerOptOut.PlayerUuids, token);

    public async Task<int> RemoveOptedOut(IEnumerable<string> uuids, CancellationToken token = default)
    {
        await cleanupLock.WaitAsync(token);
        try
        {
            var removed = 0;
            foreach (var uuid in uuids)
            {
                token.ThrowIfCancellationRequested();
                if (cleaned.ContainsKey(uuid))
                    continue;
                try
                {
                    if (!Guid.TryParse(uuid, out var guid))
                        continue;
                    await storage.DeleteAllFlips(guid);
                    cleaned[uuid] = true;
                    removed++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger?.LogError(e, "Failed to remove flips of opted out player {Player}, will retry on next load", uuid);
                }
            }
            return removed;
        }
        finally
        {
            cleanupLock.Release();
        }
    }

    public enum EraseCheck
    {
        Ok,
        /// <summary>Body does not belong to the uuid in the route</summary>
        Mismatch,
        /// <summary>Uuid is not opted out, ingestion would keep writing</summary>
        NotOptedOut,
        /// <summary>The stored rows changed since the export</summary>
        ScopeChanged
    }

    public class PlayerExport
    {
        public Guid PlayerUuid { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public List<PastFlip> Flips { get; set; } = new();
    }

    public class EraseResult
    {
        public EraseCheck Check { get; set; }
        public int DeletedFlips { get; set; }
    }

    public async Task<PlayerExport> Export(Guid player)
    {
        return new PlayerExport
        {
            PlayerUuid = player,
            CreatedAtUtc = DateTime.UtcNow,
            Flips = (await storage.GetAllFlips(player)).ToList()
        };
    }

    /// <summary>
    /// Pure tamper recheck of an export against the route uuid and the live rows
    /// </summary>
    public static EraseCheck Validate(Guid player, PlayerExport export, IEnumerable<PastFlip> live, bool optedOut)
    {
        if (export == null || export.PlayerUuid != player || export.Flips == null || export.Flips.Any(f => f == null || f.Flipper != player))
            return EraseCheck.Mismatch;
        if (!optedOut)
            return EraseCheck.NotOptedOut;
        var exported = export.Flips.Select(f => (f.SellTime.ToUniversalTime(), f.Uid)).ToHashSet();
        var current = live.Select(f => (f.SellTime.ToUniversalTime(), f.Uid)).ToHashSet();
        return exported.SetEquals(current) ? EraseCheck.Ok : EraseCheck.ScopeChanged;
    }

    public async Task<EraseResult> Erase(Guid player, PlayerExport export)
    {
        // cheap checks first to avoid a read for foreign bodies
        var pre = Validate(player, export, export?.Flips ?? new(), PlayerOptOut.IsOptedOut(player));
        if (pre == EraseCheck.Mismatch || pre == EraseCheck.NotOptedOut)
            return new EraseResult { Check = pre };
        var live = await storage.GetAllFlips(player);
        var check = Validate(player, export, live, true);
        if (check != EraseCheck.Ok)
            return new EraseResult { Check = check };
        await storage.DeleteAllFlips(player);
        return new EraseResult { Check = check, DeletedFlips = live.Count };
    }
}
