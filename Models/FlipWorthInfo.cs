namespace Coflnet.Sky.SkyAuctionTracker.Models;

public class FlipWorthInfo
{
    public FlipWorthInfo(string playerId, long worth, DateTime time)
    {
        PlayerId = playerId;
        Worth = worth;
        Time = time;
    }
    public string PlayerId { get; set; }
    public long Worth { get; set; }
    public DateTime Time { get; set; }
}