using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class RepresentationConverter
{
    private ILogger<RepresentationConverter> logger;
    private ISniperClient sniperApi;

    public RepresentationConverter(ILogger<RepresentationConverter> logger, ISniperClient sniperApi)
    {
        this.logger = logger;
        this.sniperApi = sniperApi;
    }

    public async Task<List<SaveAuction>> ConvertToDummyAuctions(TradeModel item)
    {
        var parser = new CoinParser();
        logger.LogInformation("Got trade sell {item}", JsonConvert.SerializeObject(item));

        if (item.Received.Any(r => !parser.IsCoins(r)) || item.Received.Count == 0)
        {
            logger.LogWarning("Aborting trade save as no coins");
            return new();
        }
        logger.LogInformation("Storing trade :)");
        List<SaveAuction> auctions = null;
        var coinAmount = parser.GetInventoryCoinSum(item.Received);
        try
        {
            auctions = item.Spent.Select(sentItem =>
            {
                if (sentItem.ExtraAttributes == null)
                    return null;
                var auction = FromItemRepresent(JsonConvert.DeserializeObject<PlayerState.Client.Model.Item>(JsonConvert.SerializeObject(sentItem)));

                auction.HighestBidAmount = coinAmount;
                auction.End = item.TimeStamp;
                auction.AuctioneerId = item.MinecraftUuid.ToString("N");
                // assign a unique uuid for each dummy auction so they map to distinct UId values
                auction.Uuid = Guid.NewGuid().ToString("N");
                return (SaveAuction)auction;
            }).Where(a => a != null).ToList();

            if (auctions?.Count > 1)
            {
                var prices = await sniperApi.GetPrices(auctions) ?? [];
                var estimationSum = prices.Where(p => p != null).Select(p => p.Median).DefaultIfEmpty(0).Sum();
                logger.LogInformation("Got {count} prices for {estimationSum} {coinAmount}", prices.Count, estimationSum, coinAmount);
                // adjust each estimate based on the total estimation
                auctions.Zip(prices, (a, p) =>
                {
                    if (a == null || p == null)
                    {
                        // use average difference if no price is available
                        a.HighestBidAmount = coinAmount / auctions.Count;
                        return null;
                    }
                    var percentageOfEstimation = (float)p.Median / estimationSum;
                    a.HighestBidAmount = (long)(coinAmount * percentageOfEstimation);
                    logger.LogInformation("Adjusted price to {price} {coinAmount} {median} {estSum} {key}", a.HighestBidAmount, coinAmount, p.Median, estimationSum, p.MedianKey);
                    return a;
                }).ToList();
            }
            logger.LogInformation("Parsed trade {auction}", JsonConvert.SerializeObject(auctions));
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to store trade sell: {item}", JsonConvert.SerializeObject(item));
            await Task.Delay(300_000);
            throw;
        }
        return auctions ?? new();
    }


    public void TryUpdatingBuyState(ApiSaveAuction buy, PlayerState.Client.Model.Item itemStateAtTrade)
    {
        try
        {
            // reassign cause flattened nbt has to be copied
            var converted = FromItemRepresent(itemStateAtTrade);
            buy.FlatenedNBT = converted.FlatenedNBT;
            buy.Enchantments = converted.Enchantments;
            buy.Tier = converted.Tier;
            buy.Reforge = converted.Reforge;
            buy.ItemName = converted.ItemName;
            logger.LogInformation($"Adjusted buy state for trade {buy.Uuid} {buy.Tag} {JsonConvert.SerializeObject(itemStateAtTrade)}");
        }
        catch (System.Exception e)
        {
            logger.LogError(e, $"Could not adjust buy state for trade {buy.Uuid} {buy.Tag} {JsonConvert.SerializeObject(itemStateAtTrade)}");
        }
    }

    public ApiSaveAuction FromItemRepresent(Coflnet.Sky.PlayerState.Client.Model.Item i)
    {
        var auction = new SaveAuction()
        {
            Count = i.Count ?? 0,
            Tag = i.Tag,
            ItemName = i.ItemName,
        };
        AssignProperties(i, auction);
        return JsonConvert.DeserializeObject<ApiSaveAuction>(JsonConvert.SerializeObject(auction));
    }

    private static void AssignProperties(PlayerState.Client.Model.Item i, SaveAuction auction)
    {
        auction.Enchantments = i.Enchantments?.Select(e => new Enchantment()
        {
            Type = Enum.TryParse<Enchantment.EnchantmentType>(e.Key, out var type) ? type : Enchantment.EnchantmentType.unknown,
            Level = (byte)e.Value
        }).ToList() ?? new();
        auction.Tier = Enum.TryParse<Tier>(i.ExtraAttributes.FirstOrDefault(a => a.Key == "tier").Value?.ToString() ?? "", out var tier) ? tier : Tier.UNKNOWN;
        auction.Reforge = Enum.TryParse<ItemReferences.Reforge>(i.ExtraAttributes.FirstOrDefault(a => a.Key == "modifier").Value?.ToString() ?? "", out var reforge) ? reforge : ItemReferences.Reforge.Unknown;
        i.ExtraAttributes.Remove("modifier");
        auction.SetFlattenedNbt(NBT.FlattenNbtData(NBT.FromDeserializedJson(i.ExtraAttributes)));
    }
}
