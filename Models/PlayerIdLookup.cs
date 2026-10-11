namespace Coflnet.Sky.SkyAuctionTracker.Models;

/// <summary>
/// Maps the lossy player hash stored in <see cref="FlipEvent.PlayerId"/> back to a player uuid.
/// Only players that bought or sold something (or were seen as auctioneer) are recorded,
/// recipients that never did anything stay hashes. Rows expire after 90 days, opted out players are never stored.
/// </summary>
public class PlayerIdLookup
{
    /// <summary>
    /// Hash as produced by <c>TrackerService.GetId(uuid)</c>
    /// </summary>
    public long Hash { get; set; }
    /// <summary>
    /// The player uuid
    /// </summary>
    public Guid Uuid { get; set; }
    /// <summary>
    /// When the player was last seen
    /// </summary>
    public DateTime LastSeen { get; set; }
}
