namespace Coflnet.Sky.SkyAuctionTracker.Models;
public class FlipTimeSelection
{
    /// <summary>
    /// The player to get the flips for
    /// </summary>
    public long PlayerId { get; set; }
    /// <summary>
    /// The start of the time range
    /// </summary>
    public DateTime Start { get; set; }
    /// <summary>
    /// The end of the time range
    /// </summary>
    public DateTime End { get; set; }
}
