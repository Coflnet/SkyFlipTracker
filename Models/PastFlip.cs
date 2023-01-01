
using System;
using System.Text.Json.Serialization;
using Coflnet.Sky.Core;
using MessagePack;
using System.Collections.Generic;
namespace Coflnet.Sky.SkyAuctionTracker.Models;

public class PastFlip
{
    /// <summary>
    /// The uuid of the player that sold the item
    /// </summary>
    [Cassandra.Mapping.Attributes.PartitionKey]
    public Guid Flipper { get; set; }
    /// <summary>
    /// The name of the sold item
    /// </summary>
    public string ItemName { get; set; }
    /// <summary>
    /// The tier of the sold item
    /// </summary>
    public Tier ItemTier { get; set; } 
    /// <summary>
    /// Hypixel item tag
    /// </summary>
    public string ItemTag { get; set; }
    public long PurchaseAuctionId { get; set; }
    public long PurchaseCost { get; set; }
    public DateTime PurchaseTime { get; set; }
    public long TargetPrice { get; set; }
    public LowPricedAuction.FinderType FinderType { get; set; }
    public long SellAuctionId { get; set; }
    public long SellPrice { get; set; }
    [Cassandra.Mapping.Attributes.ClusteringKey(0)]
    public DateTime SellTime { get; set; }
    /// <summary>
    /// The uid of the sold item
    /// </summary>
    [Cassandra.Mapping.Attributes.ClusteringKey(1)]
    public long Uid { get; set; }
    /// <summary>
    /// The total profit of the flip
    /// </summary>
    public long Profit { get; set; }
    /// <summary>
    /// Serialized msgpack stored in cassandra
    /// </summary>
    [IgnoreMember]
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public byte[] SerialisedProfitChanges { get; set; }
    /// <summary>
    /// Things that affected the profit
    /// </summary>
    [Cassandra.Mapping.Attributes.Ignore]
    [IgnoreMember]
    public IEnumerable<ProfitChange> ProfitChanges
    {
        get
        {
            if (SerialisedProfitChanges == null)
                return null;
            return MessagePack.LZ4MessagePackSerializer.Deserialize<IEnumerable<ProfitChange>>(SerialisedProfitChanges);
        }
        set
        {
            if (SerialisedProfitChanges == null && value != null)
                SerialisedProfitChanges = MessagePack.LZ4MessagePackSerializer.Serialize<IEnumerable<ProfitChange>>(value);
        }
    }
    /// <summary>
    /// The version of the profit changes, used to request updates the profit changes
    /// </summary>
    public short Version { get; set; }

    /// <summary>
    /// A single change in the profit amount
    /// </summary>
    [MessagePackObject]
    public class ProfitChange
    {
        /// <summary>
        /// Display label for the profit change
        /// </summary>
        [Key(0)]
        public string Label { get; set; }
        /// <summary>
        /// If available the timestamp of the change
        /// eg. when a player bought an item to craft
        /// </summary>
        [Key(1)]
        public DateTime Timestamp { get; set; }
        /// <summary>
        /// The amount of the change on the profit
        /// </summary>
        [Key(2)]
        public long Amount { get; set; }
        /// <summary>
        /// The id of the item that caused the change (if available)
        /// </summary>
        [Key(3)]
        public long ContextItemId { get; set; }
    }
}