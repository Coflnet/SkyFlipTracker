
using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Coflnet.Sky.Core;
using MessagePack;
using System.Collections.Generic;

namespace Coflnet.Sky.SkyAuctionTracker.Models
{
    /// <summary>
    /// A single found flip
    /// </summary>
    [DataContract]
    public class Flip
    {
        /// <summary>
        /// Internal Id for the flip
        /// </summary>
        [IgnoreDataMember]
        [JsonIgnore]
        public int Id { get; set; }
        /// <summary>
        /// The shortened auction id (uid)
        /// </summary>
        [DataMember(Name = "auctionId")]
        public long AuctionId { get; set; }
        /// <summary>
        /// The estimated target price
        /// </summary>
        [DataMember(Name = "targetPrice")]
        public int TargetPrice { get; set; }
        /// <summary>
        /// What finder found the flip
        /// </summary>
        [DataMember(Name = "finderType")]
        public LowPricedAuction.FinderType FinderType { get; set; }
        /// <summary>
        /// when the flip was found
        /// </summary>
        [DataMember(Name = "timestamp")]
        public DateTime Timestamp { get; set; }
    }
}