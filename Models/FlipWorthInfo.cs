namespace Coflnet.Sky.SkyAuctionTracker.Models;

public class FlipWorthInfo
{
    public FlipWorthInfo(long playerId, long worth, DateTime time)
    {
        PlayerId = playerId;
        Worth = worth;
        Time = time;
    }
    public long PlayerId { get; set; }
    public long Worth { get; set; }
    public DateTime Time { get; set; }
}