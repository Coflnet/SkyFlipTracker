using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Models.Investigation;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Summarises purchase funnel entries. Pure, no io.
/// </summary>
public static class PurchaseReportBuilder
{
    public const int TopReceiverCount = 10;

    /// <summary>
    /// Builds the report summary from the entries
    /// </summary>
    public static PlayerPurchaseReport Build(IReadOnlyList<PurchaseFunnelEntry> entries)
    {
        var report = new PlayerPurchaseReport { Entries = entries.ToList(), Count = entries.Count };
        var auction = entries.Where(e => !e.ViaTrade).ToList();
        report.AuctionPurchaseCount = auction.Count;
        report.TradeCount = entries.Count - auction.Count;
        report.OtherBuyerCount = entries.Count(e => e.BoughtByOtherAccount);
        report.ProfitViaTrade = entries.Where(e => e.ViaTrade).Sum(e => e.Profit ?? 0);
        report.TotalProfit = entries.Sum(e => e.Profit ?? 0);
        if (auction.Count == 0)
            return report;
        report.FoundPct = Pct(auction.Count(e => e.Found), auction.Count);
        report.FoundBeforePurchasePct = Pct(auction.Count(e => e.Found && e.Verdict != FunnelVerdict.FoundAfterSold), auction.Count);
        report.MedianLeadSeconds = Median(auction.Where(e => e.LeadSeconds != null).Select(e => e.LeadSeconds.Value));
        report.MedianSendLeadSeconds = Median(auction.Where(e => e.SendLeadSeconds != null).Select(e => e.SendLeadSeconds.Value));
        report.ByFinder = auction.Where(e => e.Finders.Count > 0).GroupBy(e => e.Finders[0]).ToDictionary(g => g.Key, g => g.Count());
        report.ByVerdict = auction.GroupBy(e => e.Verdict.ToString()).ToDictionary(g => g.Key, g => g.Count());
        report.ProfitNotFound = auction.Where(e => e.Verdict == FunnelVerdict.NotFound).Sum(e => e.Profit ?? 0);
        report.ProfitFoundNotReceived = auction.Where(e => e.Verdict is FunnelVerdict.FoundNotSent or FunnelVerdict.SentAfterSold
            or FunnelVerdict.SentNotClicked or FunnelVerdict.ClickedNotBought).Sum(e => e.Profit ?? 0);
        return report;
    }

    /// <summary>
    /// Classifies one purchase entry. Trades get no funnel. The buyer set is the player's accounts plus the account
    /// that actually bought (the AUCTION_SOLD player), because the investigated player may be only the seller of the flip.
    /// </summary>
    /// <param name="aggregates">event aggregates of this auction</param>
    /// <param name="buyerEvents">events of this auction by the player's accounts and the sold account</param>
    public static void ClassifyEntry(PurchaseFunnelEntry entry, IEnumerable<EventAggregate> aggregates, IEnumerable<FlipEvent> buyerEvents,
        long? soldToHash, ISet<long> playerHashes, IReadOnlyCollection<Flip> flips, IReadOnlyCollection<FinderContext> contexts)
    {
        entry.SoldToHash = soldToHash;
        entry.BoughtByOtherAccount = entry.Flags.Contains(nameof(FlipFlags.DifferentBuyer))
            || soldToHash != null && !playerHashes.Contains(soldToHash.Value);
        if (entry.ViaTrade)
        {
            entry.Verdict = FunnelVerdict.Unknown;
            entry.Explanation = "acquired via trade, no auction funnel";
            return;
        }
        var buyers = new HashSet<long>(playerHashes);
        if (soldToHash != null)
            buyers.Add(soldToHash.Value);
        var stats = EventStats.FromAggregates(aggregates, buyerEvents.Where(e => buyers.Contains(e.PlayerId)));
        var result = FunnelClassifier.Classify(new FunnelInput
        {
            AuctionSold = true,
            SoldAtOverride = entry.PurchaseTime,
            Flips = flips,
            Contexts = contexts,
            Profit = entry.Profit ?? 0
        }, stats);
        entry.Verdict = result.Verdict;
        entry.Explanation = result.Explanation;
        entry.Found = result.Findings.Count > 0;
        entry.BuyerReceived = result.BuyerReceived;
        entry.BuyerClicked = result.BuyerClicked;
        entry.Finders = result.Findings.Select(f => f.Finder.ToString()).ToList();
        entry.LeadSeconds = result.Timings.FoundToSoldSeconds;
        entry.SendLeadSeconds = result.BuyerSendLeadSeconds;
    }

    /// <summary>
    /// Merges per chunk counts of (account hash, auctions received) into the top receivers.
    /// Each chunk holds distinct (auction, player) pairs so counts can be added across chunks.
    /// </summary>
    /// <param name="chunkCounts">per chunk player hash to number of auctions received</param>
    /// <param name="ownHashes">accounts of the investigated player, excluded</param>
    /// <param name="auctionsWithReceive">number of purchases that had any FLIP_RECEIVE</param>
    /// <param name="top">how many to keep</param>
    public static List<TopReceiver> MergeTopReceivers(IEnumerable<IEnumerable<KeyValuePair<long, int>>> chunkCounts,
        ISet<long> ownHashes, int auctionsWithReceive, int top = TopReceiverCount)
    {
        return chunkCounts.SelectMany(c => c)
            .Where(c => ownHashes == null || !ownHashes.Contains(c.Key))
            .GroupBy(c => c.Key)
            .Select(g => new TopReceiver { Hash = g.Key, ReceivedCount = g.Sum(c => c.Value) })
            .OrderByDescending(r => r.ReceivedCount).ThenBy(r => r.Hash)
            .Take(top)
            .Select(r =>
            {
                r.SharePct = Pct(r.ReceivedCount, auctionsWithReceive);
                return r;
            }).ToList();
    }

    private static double Pct(int part, int total) => total == 0 ? 0 : Math.Round(part * 100.0 / total, 1);

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            return null;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
