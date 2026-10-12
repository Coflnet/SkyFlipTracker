using System.Text.Json.Serialization;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.SkyAuctionTracker.Models.Investigation;

/// <summary>
/// Where in the found -> sent -> clicked -> bought chain an auction was lost (or won)
/// </summary>
public enum FunnelVerdict
{
    /// <summary>The auction is still running or has no buyer</summary>
    NotSoldYet,
    /// <summary>No finder reported the auction at all</summary>
    NotFound,
    /// <summary>Found, but only after it was bought</summary>
    FoundAfterSold,
    /// <summary>Found in time, nobody received it</summary>
    FoundNotSent,
    /// <summary>Everybody received it after it was bought</summary>
    SentAfterSold,
    /// <summary>Received by others before the sale, nobody clicked</summary>
    SentNotClicked,
    /// <summary>Clicked by somebody but not bought by the buyer through us</summary>
    ClickedNotBought,
    /// <summary>The buyer (or a linked account) received the flip from us</summary>
    BoughtByUser,
    /// <summary>Not enough data to decide</summary>
    Unknown
}

/// <summary>
/// One finder reporting an auction
/// </summary>
public class FinderFinding
{
    /// <summary>Finder that reported it</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LowPricedAuction.FinderType Finder { get; set; }
    /// <summary>Approximate found time (finder context found time or flip store time minus 3 seconds)</summary>
    public DateTime FoundTime { get; set; }
    /// <summary>"finder_context" or "flip" (store time based)</summary>
    public string TimeSource { get; set; }
    /// <summary>Target price of the finder if known</summary>
    public long? TargetPrice { get; set; }
    /// <summary>Finder specific properties</summary>
    public Dictionary<string, string> Props { get; set; }
    /// <summary>Auction context (sniper side timings)</summary>
    public Dictionary<string, string> AuctionContext { get; set; }
}

/// <summary>
/// Event counts and times of one auction
/// </summary>
public class FunnelTimeline
{
    public DateTime? AuctionStart { get; set; }
    public DateTime? AuctionEnd { get; set; }
    public DateTime? SoldAt { get; set; }
    /// <summary>Lossy hash of the buyer</summary>
    public long? SoldToHash { get; set; }
    /// <summary>Uuid of the buyer, null when unknown or suppressed</summary>
    public string SoldToUuid { get; set; }
    /// <summary>auction, lookup, suppressed or unknown</summary>
    public string SoldToSource { get; set; }
    public DateTime? FirstFound { get; set; }
    public DateTime? FirstReceive { get; set; }
    public DateTime? LastReceive { get; set; }
    public int ReceiveCount { get; set; }
    public int ClickCount { get; set; }
    public int PurchaseStartCount { get; set; }
    public int PurchaseConfirmCount { get; set; }
    /// <summary>Earliest FLIP_CLICK</summary>
    public DateTime? FirstClick { get; set; }
    /// <summary>Earliest PURCHASE_START</summary>
    public DateTime? FirstPurchaseStart { get; set; }
    /// <summary>Earliest PURCHASE_CONFIRM</summary>
    public DateTime? FirstPurchaseConfirm { get; set; }
    /// <summary>Distinct users that received (or clicked) the flip, null when only aggregates were available</summary>
    public int? ReceiverCount { get; set; }
    /// <summary>The first 50 receivers ordered by receive time</summary>
    public List<FunnelReceiver> Receivers { get; set; } = new();
    /// <summary>Users that received the flip within -3.8s/+3.9s of the sale</summary>
    public int? ReceiversNearSold { get; set; }
    /// <summary>True when more than 5000 events existed and only the first were read</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Reaction of one user that received an auction
/// </summary>
public class FunnelReceiver
{
    /// <summary>Lossy hash of the user</summary>
    public long Hash { get; set; }
    /// <summary>Time of the first FLIP_RECEIVE, the first click time when no receive event exists</summary>
    public DateTime ReceivedAt { get; set; }
    public DateTime? ClickedAt { get; set; }
    public DateTime? PurchaseStartedAt { get; set; }
    public double? ReceiveToClickSeconds { get; set; }
    /// <summary>Sold time minus receive time, positive when received before the sale</summary>
    public double? ReceiveToSoldSeconds { get; set; }
}

/// <summary>
/// Durations in seconds between the steps, null when a step is missing
/// </summary>
public class FunnelTimings
{
    public double? StartToFoundSeconds { get; set; }
    public double? FoundToFirstReceiveSeconds { get; set; }
    public double? FoundToSoldSeconds { get; set; }
    public double? FirstReceiveToSoldSeconds { get; set; }
    public double? FirstReceiveToFirstClickSeconds { get; set; }
    /// <summary>Median over the receivers that clicked</summary>
    public double? MedianReceiveToClickSeconds { get; set; }
}

/// <summary>
/// Stored unsold flip information
/// </summary>
public class UnsoldFlipInfo
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LowPricedAuction.FinderType Finder { get; set; }
    public long TargetPrice { get; set; }
    public long StartingBid { get; set; }
    public int DailyVolume { get; set; }
    public DateTime AuctionStart { get; set; }
}

/// <summary>
/// The full found -> sent -> clicked -> bought join for one auction
/// </summary>
public class FlipFunnel
{
    public string AuctionUuid { get; set; }
    /// <summary>Lossy hash used as auction id in events</summary>
    public long AuctionUid { get; set; }
    public string Tag { get; set; }
    public long? StartingBid { get; set; }
    public long? HighestBid { get; set; }
    public List<FinderFinding> Findings { get; set; } = new();
    public FunnelTimeline Timeline { get; set; } = new();
    public FunnelTimings Timings { get; set; } = new();
    public bool BuyerReceived { get; set; }
    public bool BuyerClicked { get; set; }
    /// <summary>Hashes of the accounts linked to the buyer (including the buyer)</summary>
    public List<long> BuyerLinkedAccounts { get; set; } = new();
    /// <summary>Exempt entries triggered by this auction, only filled with includeOutsped=true</summary>
    public List<OutspedFlip> Outsped { get; set; } = new();
    public UnsoldFlipInfo UnsoldFlip { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FunnelVerdict Verdict { get; set; }
    public string Explanation { get; set; }
    /// <summary>True when the outsped/exempt rule of the tracker would apply</summary>
    public bool QualifiesForExempt { get; set; }
    /// <summary>Things that could not be loaded (api failures, timeouts)</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// One purchase of a player with its funnel result
/// </summary>
public class PurchaseFunnelEntry
{
    /// <summary>Null for entries only known from the AUCTION_SOLD event (the hash is not reversible)</summary>
    public string AuctionUuid { get; set; }
    public long AuctionUid { get; set; }
    public string ItemTag { get; set; }
    public DateTime? PurchaseTime { get; set; }
    public long? Cost { get; set; }
    public long? Profit { get; set; }
    /// <summary>pastflip, event or both</summary>
    public string Source { get; set; }
    /// <summary>Names of the PastFlip flags, empty for event-only entries</summary>
    public List<string> Flags { get; set; } = new();
    /// <summary>The finder type stored on the PastFlip, null for event-only entries</summary>
    public string StoredFinder { get; set; }
    /// <summary>Acquired via trade (ViaTrade or MultiItemTrade), such entries have no auction funnel</summary>
    public bool ViaTrade { get; set; }
    /// <summary>Hash of the AUCTION_SOLD player when that event exists</summary>
    public long? SoldToHash { get; set; }
    /// <summary>The DifferentBuyer flag is set or the AUCTION_SOLD player is not one of the player's accounts</summary>
    public bool BoughtByOtherAccount { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FunnelVerdict Verdict { get; set; }
    public string Explanation { get; set; }
    public bool Found { get; set; }
    public bool BuyerReceived { get; set; }
    public bool BuyerClicked { get; set; }
    /// <summary>Finder names that found it (earliest first)</summary>
    public List<string> Finders { get; set; } = new();
    /// <summary>Seconds between found and sold</summary>
    public double? LeadSeconds { get; set; }
    /// <summary>Seconds between the buyer receiving it and sold, null when the buyer did not receive it</summary>
    public double? SendLeadSeconds { get; set; }
}

/// <summary>
/// Another account that received flips for the purchases of a player.
/// A single account receiving most of a player's purchases suggests the player gets our flips through that other account.
/// </summary>
public class TopReceiver
{
    /// <summary>Lossy hash of the receiving account</summary>
    public long Hash { get; set; }
    /// <summary>Uuid when known from the id lookup, null when unknown or opted out</summary>
    public Guid? Uuid { get; set; }
    /// <summary>For how many of the purchase auctions this account received the flip</summary>
    public int ReceivedCount { get; set; }
    /// <summary>ReceivedCount in percent of the purchases that had any FLIP_RECEIVE</summary>
    public double SharePct { get; set; }
}

/// <summary>
/// Summary of the purchases of a player
/// </summary>
public class PlayerPurchaseReport
{
    public string PlayerUuid { get; set; }
    public int Days { get; set; }
    public List<long> Hashes { get; set; } = new();
    /// <summary>All entries including trades</summary>
    public int Count { get; set; }
    /// <summary>Entries bought on the auction house (non trade), the base of the percentages, medians and verdict counts</summary>
    public int AuctionPurchaseCount { get; set; }
    /// <summary>Entries acquired via trade, not classified</summary>
    public int TradeCount { get; set; }
    /// <summary>Entries bought by another account than the investigated ones</summary>
    public int OtherBuyerCount { get; set; }
    /// <summary>Profit of the trade entries</summary>
    public long ProfitViaTrade { get; set; }
    /// <summary>Percent of purchases a finder found at all</summary>
    public double FoundPct { get; set; }
    /// <summary>Percent of purchases found before they were bought</summary>
    public double FoundBeforePurchasePct { get; set; }
    public double? MedianLeadSeconds { get; set; }
    public double? MedianSendLeadSeconds { get; set; }
    public Dictionary<string, int> ByFinder { get; set; } = new();
    public Dictionary<string, int> ByVerdict { get; set; } = new();
    /// <summary>Profit of all entries including trades</summary>
    public long TotalProfit { get; set; }
    public long ProfitNotFound { get; set; }
    public long ProfitFoundNotReceived { get; set; }
    /// <summary>Other accounts that received the flips of this player's purchases (top 10, excluding the player's own accounts)</summary>
    public List<TopReceiver> TopReceivers { get; set; } = new();
    public List<PurchaseFunnelEntry> Entries { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Event counts of one day
/// </summary>
public class DailyActivity
{
    public DateTime Day { get; set; }
    /// <summary>Event type name to count</summary>
    public Dictionary<string, int> Counts { get; set; } = new();
}

/// <summary>
/// Recent activity of a player (is the player still using the service)
/// </summary>
public class PlayerActivity
{
    public string PlayerUuid { get; set; }
    public int Days { get; set; }
    public List<long> Hashes { get; set; } = new();
    /// <summary>Event type name to time of the latest event</summary>
    public Dictionary<string, DateTime> LastEventAt { get; set; } = new();
    public List<DailyActivity> Daily { get; set; } = new();
    /// <summary>True when the player received, clicked or bought something in the last 3 days</summary>
    public bool LooksActive { get; set; }
    public List<string> Warnings { get; set; } = new();
}
