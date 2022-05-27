namespace Coflnet.Sky.SkyAuctionTracker.Models
{
    /// <summary>
    /// Different types of flip events
    /// </summary>
    public enum FlipEventType
    {
        /// <summary>
        /// Flip was sent out
        /// </summary>
        FLIP_RECEIVE = 1,
        /// <summary>
        /// User clicked on the flip
        /// </summary>
        FLIP_CLICK = 2,
        /// <summary>
        /// User started to buy the flip
        /// </summary>
        PURCHASE_START = 3,
        /// <summary>
        /// User confirmed the purchase
        /// </summary>
        PURCHASE_CONFIRM = 4,
        /// <summary>
        /// The auction was bought by the user
        /// </summary>
        AUCTION_SOLD = 5,
        /// <summary>
        /// User upvoted the flip
        /// </summary>
        UPVOTE = 6,
        /// <summary>
        /// 
        /// </summary>
        DOWNVOTE = 7
    }
}