namespace Coflnet.Sky.SkyAuctionTracker.Models;

public class ComplicatedFlip
{
    public string ItemTag { get; set; }
    public Guid AuctionId { get; set; }
    public DateTime EndedAt { get; set; }
    public long SoldFor { get; set; }
    public Dictionary<string, long> AttributeValues { get; set; }
}