namespace Coflnet.Sky.SkyAuctionTracker.Models;

public class FlipWorthInfo
{
    public FlipWorthInfo(string playerId, long worth, DateTime time, string auctionId)
    {
        PlayerId = playerId;
        Worth = worth;
        Time = time;
        AuctionId = auctionId;
    }
    public string PlayerId { get; set; }
    public string AuctionId { get; set; }
    public long Worth { get; set; }
    public DateTime Time { get; set; }
}