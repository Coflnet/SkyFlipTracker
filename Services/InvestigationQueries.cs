using Microsoft.EntityFrameworkCore;
using Coflnet.Sky.SkyAuctionTracker.Models;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>Number of distinct auctions a player received a flip for</summary>
public class ReceiverCount
{
    public long PlayerId { get; set; }
    public int Count { get; set; }
}

/// <summary>Per day, per type event count</summary>
public class ActivityRow
{
    public FlipEventType Type { get; set; }
    public DateTime Day { get; set; }
    public int Count { get; set; }
    public DateTime Last { get; set; }
}

/// <summary>
/// The EF queries of the investigation endpoints. Kept as IQueryable so a test can check they translate to SQL.
/// Aggregation happens in SQL, id lists must be chunked to <see cref="ChunkSize"/>.
/// </summary>
internal static class InvestigationQueries
{
    public const int ChunkSize = 100;
    public const int MaxEventsPerAuction = 5000;

    public static IQueryable<FlipEvent> EventsOfAuction(TrackerDbContext db, long auctionUid)
        => db.FlipEvents.AsNoTracking().Where(e => e.AuctionId == auctionUid).OrderBy(e => e.Timestamp).Take(MaxEventsPerAuction + 1);

    public static IQueryable<Flip> FlipsOfAuctions(TrackerDbContext db, List<long> auctionUids)
        => db.Flips.AsNoTracking().Where(f => auctionUids.Contains(f.AuctionId));

    public static IQueryable<EventAggregate> EventAggregates(TrackerDbContext db, List<long> auctionUids)
        => db.FlipEvents.Where(e => auctionUids.Contains(e.AuctionId) && e.Type != FlipEventType.START)
            .GroupBy(e => new { e.AuctionId, e.Type })
            .Select(g => new EventAggregate
            {
                AuctionId = g.Key.AuctionId,
                Type = g.Key.Type,
                Count = g.Count(),
                Min = g.Min(e => e.Timestamp),
                Max = g.Max(e => e.Timestamp)
            });

    public static IQueryable<FlipEvent> PlayerEventsOnAuctions(TrackerDbContext db, List<long> playerHashes, List<long> auctionUids)
        => db.FlipEvents.AsNoTracking().Where(e => auctionUids.Contains(e.AuctionId) && playerHashes.Contains(e.PlayerId));

    /// <summary>AUCTION_SOLD rows of the auctions (at most one per auction)</summary>
    public static IQueryable<FlipEvent> SoldEventsOfAuctions(TrackerDbContext db, List<long> auctionUids)
        => db.FlipEvents.AsNoTracking().Where(e => e.Type == FlipEventType.AUCTION_SOLD && auctionUids.Contains(e.AuctionId));

    public static IQueryable<FlipEvent> SoldEventsOfPlayers(TrackerDbContext db, List<long> playerHashes, DateTime since, int limit)
        => db.FlipEvents.AsNoTracking().Where(e => e.Type == FlipEventType.AUCTION_SOLD && playerHashes.Contains(e.PlayerId) && e.Timestamp >= since)
            .OrderByDescending(e => e.Timestamp).Take(limit);

    /// <summary>
    /// For each player the number of distinct auctions of the chunk they received a flip for
    /// </summary>
    public static IQueryable<ReceiverCount> ReceiverCounts(TrackerDbContext db, List<long> auctionUids)
        => db.FlipEvents.Where(e => e.Type == FlipEventType.FLIP_RECEIVE && auctionUids.Contains(e.AuctionId))
            .Select(e => new { e.AuctionId, e.PlayerId }).Distinct()
            .GroupBy(e => e.PlayerId)
            .Select(g => new ReceiverCount { PlayerId = g.Key, Count = g.Count() });

    public static IQueryable<ActivityRow> ActivityByDay(TrackerDbContext db, List<long> playerHashes, DateTime since)
        => db.FlipEvents.Where(e => playerHashes.Contains(e.PlayerId) && e.Timestamp >= since)
            .GroupBy(e => new { e.Type, Day = e.Timestamp.Date })
            .Select(g => new ActivityRow { Type = g.Key.Type, Day = g.Key.Day, Count = g.Count(), Last = g.Max(e => e.Timestamp) });
}
