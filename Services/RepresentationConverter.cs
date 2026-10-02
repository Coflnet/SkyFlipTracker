using System.Globalization;
using System.Text.RegularExpressions;
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
    private Api.Client.Api.IPricesApi pricesApi;
    private static readonly Regex PetLevelInName = new(@"\[Lvl (\d+)\]");

    public RepresentationConverter(ILogger<RepresentationConverter> logger, ISniperClient sniperApi, Api.Client.Api.IPricesApi pricesApi = null)
    {
        this.logger = logger;
        this.sniperApi = sniperApi;
        this.pricesApi = pricesApi;
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
            auctions = item.Spent.Select((sentItem,i) =>
            {
                if (sentItem.ExtraAttributes == null)
                    return null;
                var auction = FromItemRepresent(JsonConvert.DeserializeObject<PlayerState.Client.Model.Item>(JsonConvert.SerializeObject(sentItem)));

                auction.HighestBidAmount = coinAmount;
                auction.End = item.TimeStamp;
                auction.AuctioneerId = item.MinecraftUuid.ToString("N");
                auction.UId = DateTime.UtcNow.Ticks / 10_000 + i; // ensure unique uid for each item in trade
                auction.Uuid = Guid.Empty.ToString("N");
                return (SaveAuction)auction;
            }).Where(a => a != null).ToList();

            if (auctions?.Count > 1)
            {
                var prices = await sniperApi.GetPrices(auctions) ?? [];
                var estimationSum = prices.Where(p => p != null).Select(p => p.Median).DefaultIfEmpty(0).Sum();
                logger.LogInformation("Got {count} prices for {estimationSum} {coinAmount}", prices.Count, estimationSum, coinAmount);
                if (prices.Count != auctions.Count || prices.Any(p => p == null) || estimationSum <= 0)
                {
                    auctions.ForEach(a => a.HighestBidAmount = coinAmount / auctions.Count);
                }
                else
                {
                    // adjust each estimate based on the total estimation
                    auctions.Zip(prices, (a, p) =>
                    {
                        var percentageOfEstimation = (float)p.Median / estimationSum;
                        a.HighestBidAmount = (long)(coinAmount * percentageOfEstimation);
                        logger.LogInformation("Adjusted price to {price} {coinAmount} {median} {estSum} {key}", a.HighestBidAmount, coinAmount, p.Median, estimationSum, p.MedianKey);
                        return a;
                    }).ToList();
                }
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

    /// <summary>
    /// Splits <paramref name="total"/> across the items by their share of the estimated value.
    /// Each item takes the first estimate available: the sniper median, a tag-level price, an even share.
    /// </summary>
    /// <param name="items">The traded items, null where the item state is unknown</param>
    /// <param name="total">The coins to split</param>
    /// <returns>One amount per item, uncertain where only the even share was available</returns>
    public async Task<List<(long cost, bool uncertain)>> SplitByEstimate(List<SaveAuction> items, long total)
    {
        foreach (var pet in items.Where(IsPet))
            CompletePetAttributes(pet);
        var estimates = await GetSniperMedians(items);
        for (var i = 0; i < items.Count; i++)
            if (estimates[i] <= 0 && items[i] != null)
                estimates[i] = await GetTagEstimate(items[i]);
        var unpriced = estimates.Select(e => e <= 0).ToList();
        var evenShare = EvenShare(total, estimates.Sum(), unpriced.Count(u => u), items.Count);
        var weights = estimates.Select((e, i) => unpriced[i] ? evenShare : e).ToList();
        var weightSum = weights.Sum();
        logger.LogInformation("Split {total} over {itemCount} trade items, {unpriced} without estimate", total, items.Count, unpriced.Count(u => u));
        return weights.Select((w, i) => ((long)(weightSum > 0 ? total * (w / weightSum) : total / items.Count), unpriced[i])).ToList();
    }

    /// <summary>
    /// Even share for items without estimate: the mean of the coins the estimates left uncovered and of the total, each split evenly.
    /// </summary>
    private static decimal EvenShare(long total, decimal estimated, int unpricedCount, int itemCount)
    {
        if (unpricedCount == 0)
            return 0;
        return (Math.Max(0, total - estimated) / unpricedCount + (decimal)total / itemCount) / 2;
    }

    /// <returns>The sniper median of each item, 0 where the sniper has none or can not see the item's full attributes</returns>
    private async Task<List<decimal>> GetSniperMedians(List<SaveAuction> items)
    {
        var medians = items.Select(_ => 0m).ToList();
        var visible = items.Select((item, index) => (item, index))
            .Where(p => p.item != null && (!IsPet(p.item) || p.item.FlatenedNBT.ContainsKey("exp"))).ToList();
        List<Sniper.Client.Model.PriceEstimate> prices;
        try
        {
            prices = visible.Count == 0 ? [] : await sniperApi.GetPrices(visible.Select(p => p.item)) ?? [];
        }
        catch (Exception e)
        {
            // the purchase is still tracked, by the remaining estimates
            logger.LogError(e, "Sniper could not price {count} trade items", visible.Count);
            return medians;
        }
        if (prices.Count != visible.Count)
            return medians;
        foreach (var (price, target) in prices.Zip(visible))
            medians[target.index] = price?.Median ?? 0;
        return medians;
    }

    /// <summary>
    /// Tag-level price with as many attribute filters as still find sales, only a pet never drops its first one.
    /// </summary>
    private async Task<decimal> GetTagEstimate(SaveAuction item)
    {
        if (pricesApi == null)
            return 0;
        var filters = TagFilters(item);
        // sales of some tags are stored without rarity, a pet's price depends on it too much to go without
        var required = IsPet(item) ? Math.Min(1, filters.Count) : 0;
        for (var count = filters.Count; count >= required; count--)
        {
            try
            {
                var price = await pricesApi.ApiItemPriceItemTagGetAsync(item.Tag, filters.Take(count).ToDictionary(f => f.Key, f => f.Value));
                if (price?.Median > 0)
                    return (decimal)price.Median * Math.Max(1, item.Count);
            }
            catch (Api.Client.Client.ApiException e)
            {
                logger.LogWarning(e, "No tag estimate for {tag} with {count} filters", item.Tag, count);
            }
        }
        return 0;
    }

    /// <returns>Filters ordered by how much they change the price</returns>
    private static List<KeyValuePair<string, string>> TagFilters(SaveAuction item)
    {
        var filters = new List<KeyValuePair<string, string>>();
        if (item.Tier != Tier.UNKNOWN)
            filters.Add(new("Rarity", item.Tier.ToString()));
        if (IsPet(item) && GetPetLevel(item) is int level)
            filters.Add(new("PetLevel", level.ToString()));
        if (item.Reforge != ItemReferences.Reforge.None && item.Reforge != ItemReferences.Reforge.Unknown)
            filters.Add(new("Reforge", item.Reforge.ToString()));
        return filters;
    }

    private static bool IsPet(SaveAuction item)
    {
        return item?.Tag?.StartsWith("PET_") == true && !item.Tag.StartsWith("PET_ITEM_") && !item.Tag.StartsWith("PET_SKIN_");
    }

    /// <summary>
    /// Makes rarity and level visible to the price lookups, pets keep both in petInfo or only in their name
    /// </summary>
    private static void CompletePetAttributes(SaveAuction pet)
    {
        if (pet.Tier == Tier.UNKNOWN && Enum.TryParse<Tier>(pet.FlatenedNBT.GetValueOrDefault("tier"), out var tier))
            pet.Tier = tier;
        if (!pet.FlatenedNBT.ContainsKey("exp") && GetPetLevel(pet) >= MaxPetLevel(pet))
            pet.FlatenedNBT["exp"] = MaxPetExp(pet).ToString();
    }

    /// <returns>The level from petInfo.exp when it is maxed, else the [Lvl N] of the item name, null when neither is known</returns>
    private static int? GetPetLevel(SaveAuction pet)
    {
        if (double.TryParse(pet.FlatenedNBT.GetValueOrDefault("exp"), NumberStyles.Float, CultureInfo.InvariantCulture, out var exp) && exp >= MaxPetExp(pet))
            return MaxPetLevel(pet);
        var match = PetLevelInName.Match(pet.ItemName ?? "");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static int MaxPetLevel(SaveAuction pet) => pet.Tag == "PET_GOLDEN_DRAGON" ? 200 : 100;

    private static int MaxPetExp(SaveAuction pet) => pet.Tag == "PET_GOLDEN_DRAGON" ? ProfitChangeService.ExpMaxLevelGoldenDragon : ProfitChangeService.ExpPetMaxLevel;


    public void TryUpdatingBuyState(ApiSaveAuction buy, PlayerState.Client.Model.Item itemStateAtTrade, List<PlayerState.Client.Model.Transaction> itemTrade)
    {
        try
        {
            // reassign cause flattened nbt has to be copied
            var converted = FromItemRepresent(itemStateAtTrade);
            buy.FlatenedNBT = converted.FlatenedNBT;
            if(buy.FlatenedNBT.TryGetValue("hot_potato_count", out var hpc))
            {
                buy.FlatenedNBT["hpc"] = hpc;
            }
            buy.Enchantments = converted.Enchantments;
            buy.Tier = converted.Tier;
            buy.Reforge = converted.Reforge;
            buy.ItemName = converted.ItemName;
            buy.Tag = converted.Tag;
            if(itemTrade != null && itemTrade.Count > 0)
            {
                var tradeTime = itemTrade.First().TimeStamp;
                buy.End = tradeTime;
            }
            logger.LogInformation($"Adjusted buy state for trade {buy.Uuid} {buy.Tag} {JsonConvert.SerializeObject(itemStateAtTrade)} to {JsonConvert.SerializeObject(buy)}");
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
