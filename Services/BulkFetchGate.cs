using System;
using System.Threading;
using System.Threading.Tasks;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Limits how many "get everything" responses (all crafts, all kat costs, all bazaar prices) are downloaded and deserialized at once.
/// Every sell fetches these lists again and they are several MB of json each (the raw string plus the object graph),
/// so unbounded parallel sells multiplied them into hundreds of MB.
/// </summary>
public class BulkFetchGate
{
    /// <summary>Process wide gate, the lists are the same for every consumer</summary>
    public static readonly BulkFetchGate Shared = new(2);

    private readonly SemaphoreSlim slots;

    public BulkFetchGate(int maxConcurrent)
    {
        slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>Slots that are free right now</summary>
    public int Available => slots.CurrentCount;

    public async Task<T> Run<T>(Func<Task<T>> fetch)
    {
        await slots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }
}
