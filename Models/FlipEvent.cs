
using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Coflnet.Sky.SkyAuctionTracker.Models
{
    /// <summary>
    /// 
    /// </summary>
    [DataContract]
    public class FlipEvent
    {
        /// <summary>
        /// Internal id of the event
        /// </summary>
        [IgnoreDataMember]
        [JsonIgnore]
        public int Id { get; set; }
        /// <summary>
        /// Player triggering the event
        /// </summary>
        [DataMember(Name = "playerId")]
        public long PlayerId { get; set; }
        /// <summary>
        /// Uid of the auction
        /// </summary>
        [DataMember(Name = "auctionId")]
        public long AuctionId { get; set; }
        /// <summary>
        /// The type of the event
        /// </summary>
        [DataMember(Name = "type")]
        public FlipEventType Type { get; set; }
        /// <summary>
        /// When the event was created
        /// </summary>
        [DataMember(Name = "timestamp")]
        public DateTime Timestamp { get; set; }
    }
}