using Coflnet.Sky.Core;
namespace Coflnet.Sky.SkyAuctionTracker.Models;

/// <summary>
/// Context for a found flip
/// </summary>
public class FinderContext
{
    public Guid AuctionId { get; set; }
    public LowPricedAuction.FinderType Finder { get; set; }
    public DateTime FoundTime { get; set; }
    public Dictionary<string, string> Context { get; set; }
    public Dictionary<string, string> AuctionContext { get; set; }
}