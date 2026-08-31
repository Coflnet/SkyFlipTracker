using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.SkyAuctionTracker.Models;

/// <summary>
/// Read-only, privacy-bounded evidence for a missed flip.
/// </summary>
public class MissedFlipDto
{
    /// <summary>The requested missed-flip cohort.</summary>
    public string Cohort { get; init; }
    /// <summary>The Hypixel item tag.</summary>
    public string ItemTag { get; init; }
    /// <summary>The item tier at sale time.</summary>
    public Tier ItemTier { get; init; }
    /// <summary>The public purchase auction UUID.</summary>
    public Guid PurchaseAuctionId { get; init; }
    /// <summary>The purchase price.</summary>
    public long PurchasePrice { get; init; }
    /// <summary>The purchase time.</summary>
    public DateTime PurchaseTime { get; init; }
    /// <summary>The public sale auction UUID.</summary>
    public Guid SaleAuctionId { get; init; }
    /// <summary>The sale price.</summary>
    public long SalePrice { get; init; }
    /// <summary>The sale time.</summary>
    public DateTime SaleTime { get; init; }
    /// <summary>The target price captured with the flip.</summary>
    public long TargetPrice { get; init; }
    /// <summary>The finder classification stored with the flip.</summary>
    public LowPricedAuction.FinderType FinderType { get; init; }
    /// <summary>The stored profit.</summary>
    public long Profit { get; init; }
    /// <summary>The stored flip flags.</summary>
    public FlipFlags Flags { get; init; }
    /// <summary>Privacy-bounded profit changes.</summary>
    public IReadOnlyList<MissedFlipProfitChangeDto> ProfitChanges { get; init; }

    internal static MissedFlipDto FromPastFlip(PastFlip flip, string cohort)
    {
        return new MissedFlipDto
        {
            Cohort = cohort,
            ItemTag = flip.ItemTag,
            ItemTier = flip.ItemTier,
            PurchaseAuctionId = flip.PurchaseAuctionId,
            PurchasePrice = flip.PurchaseCost,
            PurchaseTime = flip.PurchaseTime,
            SaleAuctionId = flip.SellAuctionId,
            SalePrice = flip.SellPrice,
            SaleTime = flip.SellTime,
            TargetPrice = flip.TargetPrice,
            FinderType = flip.FinderType,
            Profit = flip.Profit,
            Flags = flip.Flags,
            ProfitChanges = flip.ProfitChanges?.Select(change => new MissedFlipProfitChangeDto
            {
                Label = change.Label,
                Amount = change.Amount
            }).ToList() ?? []
        };
    }
}

/// <summary>
/// The non-identifying portion of a stored profit change.
/// </summary>
public class MissedFlipProfitChangeDto
{
    /// <summary>The display label.</summary>
    public string Label { get; init; }
    /// <summary>The profit adjustment amount.</summary>
    public long Amount { get; init; }
}
