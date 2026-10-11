using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Picks the players whose hash to uuid mapping should be stored, with a 24 hour in-memory dedupe.
/// Opted out players are never selected.
/// </summary>
public class PlayerIdRecorder
{
    /// <summary>Shared instance, <see cref="TrackerService"/> is transient</summary>
    public static readonly PlayerIdRecorder Shared = new();
    internal const int MaxEntries = 200_000;
    internal static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly Dictionary<long, DateTime> seen = new();
    private readonly object sync = new();

    internal int Count { get { lock (sync) return seen.Count; } }

    /// <summary>
    /// Returns the (hash, uuid) pairs that have not been recorded in the last 24 hours
    /// </summary>
    public List<(long, Guid)> Select(IEnumerable<string> uuids, Func<string, long> getId, DateTime now)
    {
        var result = new List<(long, Guid)>();
        lock (sync)
        {
            foreach (var raw in uuids.Where(u => !string.IsNullOrEmpty(u)).Distinct())
            {
                if (!Guid.TryParse(raw, out var uuid) || uuid == Guid.Empty || Coflnet.Sky.Core.PlayerOptOut.IsOptedOut(uuid))
                    continue;
                long hash;
                try
                {
                    hash = getId(uuid.ToString("N"));
                }
                catch (Exception)
                {
                    continue;
                }
                if (seen.TryGetValue(hash, out var last) && now - last < Window)
                    continue;
                if (!seen.ContainsKey(hash) && seen.Count >= MaxEntries)
                {
                    foreach (var key in seen.Where(s => now - s.Value >= Window).Select(s => s.Key).ToList())
                        seen.Remove(key);
                    if (seen.Count >= MaxEntries)
                        seen.Clear(); // bounded memory is more important than perfect dedupe
                }
                seen[hash] = now;
                result.Add((hash, uuid));
            }
        }
        return result;
    }

    /// <summary>
    /// Selects and stores in the background, failures are logged and never thrown
    /// </summary>
    public void Record(IEnumerable<string> uuids, FlipStorageService storage, ILogger logger)
    {
        try
        {
            var selected = Select(uuids, TrackerService.HashId, DateTime.UtcNow);
            if (selected.Count == 0)
                return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await storage.SavePlayerIds(selected);
                }
                catch (Exception e)
                {
                    logger?.LogWarning(e, "Could not save {Count} player ids", selected.Count);
                }
            });
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Could not record player ids");
        }
    }
}
