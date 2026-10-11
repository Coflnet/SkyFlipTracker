using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Models.Investigation;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Event counts and times of one auction, either built from raw events or from SQL aggregates
/// </summary>
public class EventStats
{
    public int Total { get; set; }
    public int ReceiveCount { get; set; }
    public int ClickCount { get; set; }
    public int PurchaseStartCount { get; set; }
    public int PurchaseConfirmCount { get; set; }
    public int SoldCount { get; set; }
    public DateTime? FirstReceive { get; set; }
    public DateTime? LastReceive { get; set; }
    /// <summary>Earliest AUCTION_SOLD timestamp</summary>
    public DateTime? SoldAt { get; set; }
    public long? SoldPlayerId { get; set; }
    /// <summary>How often the buyer (or a linked account) received the flip</summary>
    public int BuyerReceiveCount { get; set; }
    public int BuyerClickCount { get; set; }
    public DateTime? BuyerFirstReceive { get; set; }
    public int? ReceiversNearSold { get; set; }

    /// <summary>
    /// Builds the stats from SQL aggregates (one row per type) plus the raw events of the buyer accounts
    /// </summary>
    public static EventStats FromAggregates(IEnumerable<EventAggregate> aggregates, IEnumerable<FlipEvent> buyerEvents)
    {
        var aggs = aggregates.ToList();
        var stats = new EventStats { Total = aggs.Sum(a => a.Count) };
        EventAggregate Of(FlipEventType type) => aggs.FirstOrDefault(a => a.Type == type);
        stats.ReceiveCount = Of(FlipEventType.FLIP_RECEIVE)?.Count ?? 0;
        stats.ClickCount = Of(FlipEventType.FLIP_CLICK)?.Count ?? 0;
        stats.PurchaseStartCount = Of(FlipEventType.PURCHASE_START)?.Count ?? 0;
        stats.PurchaseConfirmCount = Of(FlipEventType.PURCHASE_CONFIRM)?.Count ?? 0;
        stats.SoldCount = Of(FlipEventType.AUCTION_SOLD)?.Count ?? 0;
        stats.FirstReceive = Of(FlipEventType.FLIP_RECEIVE)?.Min;
        stats.LastReceive = Of(FlipEventType.FLIP_RECEIVE)?.Max;
        stats.SoldAt = Of(FlipEventType.AUCTION_SOLD)?.Min;
        var own = buyerEvents.ToList();
        stats.SoldPlayerId = own.Where(e => e.Type == FlipEventType.AUCTION_SOLD && e.Timestamp == stats.SoldAt).Select(e => (long?)e.PlayerId).FirstOrDefault();
        var receives = own.Where(e => e.Type == FlipEventType.FLIP_RECEIVE).ToList();
        stats.BuyerReceiveCount = receives.Count;
        stats.BuyerClickCount = own.Count(e => e.Type == FlipEventType.FLIP_CLICK);
        if (receives.Count > 0)
            stats.BuyerFirstReceive = receives.Min(e => e.Timestamp);
        return stats;
    }

    /// <summary>
    /// Builds the stats from raw events of one auction
    /// </summary>
    public static EventStats FromEvents(IReadOnlyCollection<FlipEvent> events, ISet<long> buyerHashes)
    {
        buyerHashes ??= new HashSet<long>();
        var stats = new EventStats { Total = events.Count };
        var sold = events.Where(e => e.Type == FlipEventType.AUCTION_SOLD).OrderBy(e => e.Timestamp).FirstOrDefault();
        var receives = events.Where(e => e.Type == FlipEventType.FLIP_RECEIVE).ToList();
        var clicks = events.Where(e => e.Type == FlipEventType.FLIP_CLICK).ToList();
        stats.ReceiveCount = receives.Count;
        stats.ClickCount = clicks.Count;
        stats.PurchaseStartCount = events.Count(e => e.Type == FlipEventType.PURCHASE_START);
        stats.PurchaseConfirmCount = events.Count(e => e.Type == FlipEventType.PURCHASE_CONFIRM);
        stats.SoldCount = events.Count(e => e.Type == FlipEventType.AUCTION_SOLD);
        if (receives.Count > 0)
        {
            stats.FirstReceive = receives.Min(e => e.Timestamp);
            stats.LastReceive = receives.Max(e => e.Timestamp);
        }
        stats.SoldAt = sold?.Timestamp;
        stats.SoldPlayerId = sold?.PlayerId;
        var buyerReceives = receives.Where(e => buyerHashes.Contains(e.PlayerId)).ToList();
        stats.BuyerReceiveCount = buyerReceives.Count;
        stats.BuyerClickCount = clicks.Count(e => buyerHashes.Contains(e.PlayerId));
        if (buyerReceives.Count > 0)
            stats.BuyerFirstReceive = buyerReceives.Min(e => e.Timestamp);
        if (sold != null)
            stats.ReceiversNearSold = receives.Count(f => f.Timestamp - TimeSpan.FromSeconds(3.8) < sold.Timestamp
                && f.Timestamp + TimeSpan.FromSeconds(3.9) > sold.Timestamp);
        return stats;
    }
}

/// <summary>
/// One row of a SQL <c>GroupBy(AuctionId, Type)</c> aggregate
/// </summary>
public class EventAggregate
{
    public long AuctionId { get; set; }
    public FlipEventType Type { get; set; }
    public int Count { get; set; }
    public DateTime Min { get; set; }
    public DateTime Max { get; set; }
}

/// <summary>
/// Input for <see cref="FunnelClassifier"/>
/// </summary>
public class FunnelInput
{
    public DateTime? AuctionStart { get; set; }
    /// <summary>True when the auction is known to have a buyer, false when known not, null when unknown</summary>
    public bool? AuctionSold { get; set; }
    /// <summary>Sold time to use when there is no AUCTION_SOLD event</summary>
    public DateTime? SoldAtOverride { get; set; }
    public IReadOnlyCollection<Flip> Flips { get; set; } = Array.Empty<Flip>();
    public IReadOnlyCollection<FinderContext> Contexts { get; set; } = Array.Empty<FinderContext>();
    public long Profit { get; set; }
    public DateTime Now { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of the classification
/// </summary>
public class FunnelResult
{
    public FunnelVerdict Verdict { get; set; }
    public string Explanation { get; set; }
    /// <summary>The buyer did not receive the flip and the first send was shortly after the sale (tracker exempt rule)</summary>
    public bool QualifiesForExempt { get; set; }
    public List<FinderFinding> Findings { get; set; } = new();
    public DateTime? FirstFound { get; set; }
    public DateTime? SoldAt { get; set; }
    public FunnelTimings Timings { get; set; } = new();
    public bool BuyerReceived { get; set; }
    public bool BuyerClicked { get; set; }
    /// <summary>Seconds between the buyer receiving the flip and the sale</summary>
    public double? BuyerSendLeadSeconds { get; set; }
}

/// <summary>
/// Decides where in found -> sent -> clicked -> bought an auction was lost. Pure, no io.
/// </summary>
public static class FunnelClassifier
{
    private static readonly TimeSpan StoreDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Classifies with raw events of the auction
    /// </summary>
    public static FunnelResult Classify(FunnelInput input, IReadOnlyCollection<FlipEvent> events, ISet<long> buyerHashes)
        => Classify(input, EventStats.FromEvents(events, buyerHashes));

    /// <summary>
    /// Classifies with pre aggregated event stats
    /// </summary>
    public static FunnelResult Classify(FunnelInput input, EventStats stats)
    {
        var result = new FunnelResult();
        result.Findings = BuildFindings(input);
        result.FirstFound = result.Findings.Count == 0 ? null : result.Findings.Min(f => f.FoundTime);
        var soldAt = stats.SoldAt ?? input.SoldAtOverride;
        result.SoldAt = soldAt;
        result.BuyerReceived = stats.BuyerReceiveCount > 0;
        result.BuyerClicked = stats.BuyerClickCount > 0;
        result.BuyerSendLeadSeconds = Seconds(soldAt, stats.BuyerFirstReceive);
        var timings = result.Timings;
        timings.StartToFoundSeconds = Seconds(result.FirstFound, input.AuctionStart);
        timings.FoundToFirstReceiveSeconds = Seconds(stats.FirstReceive, result.FirstFound);
        timings.FoundToSoldSeconds = Seconds(soldAt, result.FirstFound);
        timings.FirstReceiveToSoldSeconds = Seconds(soldAt, stats.FirstReceive);

        var diff = soldAt - stats.FirstReceive;
        // exact predicate the tracker used to decide to exempt a flip
        result.QualifiesForExempt = !result.BuyerReceived && diff > TimeSpan.FromSeconds(-4)
            && (diff < TimeSpan.FromSeconds(-3.2) || input.Profit > 50_000_000 && diff < TimeSpan.FromSeconds(-0.1));

        if (soldAt == null)
        {
            if (input.AuctionSold == false)
            {
                result.Verdict = FunnelVerdict.NotSoldYet;
                result.Explanation = "The auction has no buyer yet.";
            }
            else
            {
                result.Verdict = FunnelVerdict.Unknown;
                result.Explanation = "No sale time is known (no AUCTION_SOLD event and no auction data).";
            }
            return result;
        }
        if (result.Findings.Count == 0)
        {
            result.Verdict = FunnelVerdict.NotFound;
            result.Explanation = "No finder reported this auction before it was bought.";
        }
        else if (result.FirstFound > soldAt)
        {
            result.Verdict = FunnelVerdict.FoundAfterSold;
            result.Explanation = $"First found by {result.Findings.OrderBy(f => f.FoundTime).First().Finder} {Fmt(-timings.FoundToSoldSeconds)}s after it was bought.";
        }
        else if (stats.ReceiveCount == 0)
        {
            result.Verdict = FunnelVerdict.FoundNotSent;
            result.Explanation = $"Found {Fmt(timings.FoundToSoldSeconds)}s before it was bought but sent to nobody.";
        }
        else if (result.BuyerReceived)
        {
            result.Verdict = FunnelVerdict.BoughtByUser;
            result.Explanation = $"The buyer received the flip {Fmt(Seconds(soldAt, stats.BuyerFirstReceive))}s before buying it (found {Fmt(timings.FoundToSoldSeconds)}s before the sale).";
        }
        else if (stats.FirstReceive > soldAt)
        {
            result.Verdict = FunnelVerdict.SentAfterSold;
            result.Explanation = $"Found {Fmt(timings.FoundToSoldSeconds)}s before the sale but the first send was {Fmt(-timings.FirstReceiveToSoldSeconds)}s after it.";
        }
        else if (stats.ClickCount == 0)
        {
            result.Verdict = FunnelVerdict.SentNotClicked;
            result.Explanation = $"Sent to {stats.ReceiveCount} users, first {Fmt(timings.FirstReceiveToSoldSeconds)}s before the sale, nobody clicked.";
        }
        else
        {
            result.Verdict = FunnelVerdict.ClickedNotBought;
            result.Explanation = $"Sent to {stats.ReceiveCount} users and clicked {stats.ClickCount} times but the buyer did not receive it through us.";
        }
        return result;
    }

    private static string Fmt(double? seconds) => seconds?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "?";

    private static double? Seconds(DateTime? later, DateTime? earlier)
        => later == null || earlier == null ? null : Math.Round((later.Value - earlier.Value).TotalSeconds, 3);

    private static DateTime Utc(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);

    /// <summary>
    /// Prefers finder contexts (closest to the real found time), falls back to the flip store time minus 3 seconds
    /// </summary>
    internal static List<FinderFinding> BuildFindings(FunnelInput input)
    {
        var result = new List<FinderFinding>();
        foreach (var context in input.Contexts ?? Array.Empty<FinderContext>())
        {
            var flip = input.Flips?.FirstOrDefault(f => f.FinderType == context.Finder);
            result.Add(new FinderFinding
            {
                Finder = context.Finder,
                FoundTime = Utc(context.FoundTime),
                TimeSource = "finder_context",
                TargetPrice = flip?.TargetPrice,
                Props = context.Context,
                AuctionContext = context.AuctionContext
            });
        }
        foreach (var flip in input.Flips ?? Array.Empty<Flip>())
        {
            if (result.Any(r => r.Finder == flip.FinderType))
                continue;
            result.Add(new FinderFinding
            {
                Finder = flip.FinderType,
                FoundTime = Utc(flip.Timestamp) - StoreDelay,
                TimeSource = "flip",
                TargetPrice = flip.TargetPrice
            });
        }
        return result.OrderBy(f => f.FoundTime).ToList();
    }
}
