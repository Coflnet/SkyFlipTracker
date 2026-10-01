namespace Coflnet.Sky.SkyAuctionTracker.Models;

[Flags]
public enum FlipFlags
{
    None = 0,
    DifferentBuyer = 1,
    ViaTrade = 2,
    /// <summary>
    /// More than one item was traded, not exact price
    /// </summary>
    MultiItemTrade = 4,
    /// <summary>PurchaseCost is -1 and Profit is a placeholder, not a measured outcome.</summary>
    UnknownCost = 8,
}
