namespace Coflnet.Sky.SkyAuctionTracker.Models;

/// <summary>
/// Tracked flip that was found but sniped by somebody else
/// </summary>
public class OutspedFlip
{
    /// <summary>
    /// Tag of the item
    /// </summary>
    public string ItemTag { get; set; }
    /// <summary>
    /// Relevant modifier key from the sniper
    /// </summary>
    public string Key { get; set; }
    public Guid TriggeredBy { get; set; }
    public DateTime Time { get; set; }
}
