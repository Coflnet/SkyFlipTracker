using Coflnet.Sky.Core;
using MessagePack;
namespace Coflnet.Sky.SkyAuctionTracker.Models;

[MessagePackObject]
public class TradeModel
{
    [Key(0)]
    public string UserId { get; set; }
    [Key(1)]
    public Guid MinecraftUuid { get; set; }
    [Key(2)]
    public List<Item> Spent { get; set; }
    [Key(3)]
    public List<Item> Received { get; set; }
    [Key(4)]
    public string OtherSide { get; set; }
    [Key(5)]
    public DateTime TimeStamp { get; set; }
}