
using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Coflnet.Sky.SkyAuctionTracker.Models
{
    [DataContract]
    public class FlipEvent
    {
        [IgnoreDataMember]
        [JsonIgnore]
        public int Id { get; set; }
        [DataMember(Name = "playerId")]
        public long PlayerId { get; set; }
        [DataMember(Name = "auctionId")]
        public long AuctionId { get; set; }
        [DataMember(Name = "type")]
        public FlipEventType Type { get; set; }
        [System.ComponentModel.DataAnnotations.Timestamp]
        [DataMember(Name = "timestamp")]
        public DateTime Timestamp { get; set; }
    }

    public enum FlipEventType
    {
        FLIP_RECEIVE = 1,
        FLIP_CLICK = 2,
        PURCHASE_START = 3,
        PURCHASE_CONFIRM = 4,
        AUCTION_SOLD = 5,
        UPVOTE = 6,
        DOWNVOTE = 7
    }
}