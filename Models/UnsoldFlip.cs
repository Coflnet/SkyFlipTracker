using Coflnet.Sky.Core;
using MessagePack;
using Newtonsoft.Json;

namespace Coflnet.Sky.SkyAuctionTracker.Models;

public class UnsoldFlip
{
    MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
    public LowPricedAuction Flip
    {
        get => MessagePackSerializer.Deserialize<LowPricedAuction>(SerializedAuction, options);
        set => SerializedAuction = MessagePackSerializer.Serialize(value, options);
    }
    [JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] SerializedAuction { get; set; }
    public DateTime AuctionStart { get; set; }
    public long Uid { get; set; }
    [JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public int Slot { get; set; } = 0;

    public UnsoldFlip() { }
    
    public UnsoldFlip(LowPricedAuction flip)
    {
        AuctionStart = flip.Auction.Start.RoundDown(TimeSpan.FromSeconds(1));
        Uid = flip.Auction.UId;
        Slot = 0;
        Flip = flip;
    }
}