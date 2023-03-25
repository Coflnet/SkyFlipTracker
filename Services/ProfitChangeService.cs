using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.Api.Client.Model;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Items.Client.Api;
using System.Text.RegularExpressions;

namespace Coflnet.Sky.SkyAuctionTracker.Services;
/// <summary>
/// Organizses any and every profit changes
/// </summary>
public class ProfitChangeService
{
    private IPricesApi pricesApi;
    private Crafts.Client.Api.IKatApi katApi;
    private ICraftsApi craftsApi;
    private IItemsApi itemApi;
    private readonly ILogger<ProfitChangeService> logger;
    /// <summary>
    /// Keys containing itemTags that can be removed
    /// </summary>
    private readonly HashSet<string> ItemKeys = new()
        {
            "drill_part_engine",
            "drill_part_fuel_tank",
            "drill_part_upgrade_module",
        };

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pricesApi"></param>
    /// <param name="katApi"></param>
    /// <param name="craftsApi"></param>
    /// <param name="logger"></param>
    /// <param name="itemApi"></param>
    public ProfitChangeService(
        IPricesApi pricesApi,
        Crafts.Client.Api.IKatApi katApi,
        ICraftsApi craftsApi,
        ILogger<ProfitChangeService> logger,
        IItemsApi itemApi)
    {
        this.pricesApi = pricesApi;
        this.katApi = katApi;
        this.craftsApi = craftsApi;
        this.logger = logger;
        this.itemApi = itemApi;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="buy"></param>
    /// <param name="sell"></param>
    /// <returns></returns>
    public async IAsyncEnumerable<PastFlip.ProfitChange> GetChanges(ColorSaveAuction buy, Coflnet.Sky.Core.SaveAuction sell)
    {
        var changes = new List<PastFlip.ProfitChange>();
        yield return new PastFlip.ProfitChange()
        {
            Amount = -sell.HighestBidAmount / 50,
            Label = "ah tax"
        };
        if (IsNotcaluclateable(sell))
            yield break;
        if (buy.Tier == Tier.UNKNOWN)
        {
            var itemMetadata = await GetItemMetadata(buy.Tag);
            buy.Tier = (Tier)itemMetadata.Tier;
        }
        if (sell.Tier == Core.Tier.UNKNOWN)
        {
            var itemMetadata = await GetItemMetadata(sell.Tag);
            sell.Tier = (Core.Tier)itemMetadata.Tier - 1;
        }
        var tagOnPurchase = buy.Tag;
        if (tagOnPurchase != sell.Tag)
        {
            if (tagOnPurchase.Contains("_WITHER_"))
            {
                // special case for wither items they are craftable into one another
                tagOnPurchase = tagOnPurchase.Substring(tagOnPurchase.IndexOf('_') + 1);
            }
            if (tagOnPurchase == "HYPERION" || tagOnPurchase == "ASTRAEA" || tagOnPurchase == "SCYLLA" || tagOnPurchase == "VALKYRIE")
            {
                tagOnPurchase = "NECRON_BLADE";
            }
            var allCrafts = await craftsApi.CraftsAllGetAsync();
            var craft = allCrafts.Where(c => c.ItemId == sell.Tag).FirstOrDefault();
            if (craft == null)
            {
                logger.LogWarning($"could not find craft for {sell.Tag} {buy.Uuid} -> {sell.Uuid}");
                yield break;
            }
            var allIngredients = craft.Ingredients.ToList();
            AddCraftPathIngredients(tagOnPurchase, allCrafts, allIngredients);
            foreach (var item in allIngredients.Where(i => i.ItemId != tagOnPurchase))
            {
                yield return await CostOf(item.ItemId, $"crafting material {item.ItemId}" + (item.Count > 1 ? $" x{item.Count}" : ""));
            }
            var itemMetadata = await GetItemMetadata(sell.Tag);
            if (((int)buy.Tier.Value - 1) < (int)sell.Tier)
            {
                if ((int)sell.Tier + 1 == (int)itemMetadata.Tier.Value)
                {
                    // the rarity upgraded due to craft
                    buy.Tier++;
                    logger.LogInformation($"upgraded rarity of {buy.Uuid} due to craft");
                }
            }
        }

        if (sell.FlatenedNBT.ContainsKey("ability_scroll"))
        {
            var scrollsOnPurchase = buy.FlatNbt.Where(l => l.Key == "ability_scroll").SelectMany(l => l.Value.Split(' ')).ToList();
            var scrollsOnSell = sell.FlatenedNBT.Where(l => l.Key == "ability_scroll").SelectMany(l => l.Value.Split(' ')).ToList();
            var scrollsAdded = scrollsOnSell.Except(scrollsOnPurchase).ToList();
            foreach (var item in scrollsAdded)
            {
                yield return await CostOf(item, $"ability scroll {item}");
            }
        }

        if (buy.Tier.HasValue && buy.Tier.Value == Tier.UNKNOWN && buy.Tag.StartsWith("PET_"))
        {
            if (Enum.TryParse(buy.FlatNbt.Where(l => l.Key == "tier").FirstOrDefault().Value, out Tier tier))
                buy.Tier = tier;
            logger.LogInformation($"upgraded rarity to {buy.Tier} of {buy.Uuid} due to pet tier");
        }
        if (buy.Tier.HasValue && ((int)buy.Tier.Value - 1) < (int)sell.Tier)
            if (sell.Tag.StartsWith("PET_"))
            {
                if (sell.FlatenedNBT.Where(l => l.Key == "heldItem" && l.Value == "PET_ITEM_TIER_BOOST").Any())
                    yield return await CostOf("PET_ITEM_TIER_BOOST", "tier Boost cost");
                else
                {
                    Console.WriteLine($"buy tier {(int)buy.Tier.Value - 1} {buy.Tier} sell tier {(int)sell.Tier} {sell.Tier}");
                    for (int i = ((int)buy.Tier.Value - 1); i < (int)sell.Tier; i++)
                    {
                        var allCosts = await katApi.KatAllGetAsync(0, default);
                        if (allCosts == null)
                            throw new Exception("could not get kat costs from crafts api");
                        var cost = allCosts.Where(c => ((int)c.TargetRarity) > i + 1 && c.CoreData.ItemTag == sell.Tag)
                                    .OrderBy(c => c.TargetRarity).FirstOrDefault();
                        var upgradeCost = cost?.UpgradeCost;
                        var tierName = (i + 1 >= (int)Tier.LEGENDARY) ? sell.Tier.ToString() : ((Tier)i + 2).ToString();
                        var materialTitle = $"Kat materials for {tierName}";
                        var level = 1;
                        try
                        {
                            level = string.IsNullOrEmpty(buy.ItemName) ? 1 : int.Parse(Regex.Replace(buy.ItemName?.Split(' ')[1], @"[^\d]", ""));
                        }
                        catch (Exception)
                        {
                            logger.LogWarning($"could not parse level from {buy.ItemName}");
                        }
                        var costAdded = false;
                        if (cost == null || cost.MaterialCost >= int.MaxValue || level > 2)
                        {
                            // approximate cost with raw
                            var rawCost = await katApi.KatRawGetAsync();
                            var rarityInt = i + 1;
                            if (i == (int)Tier.LEGENDARY)
                                break;
                            //  rarityInt = (int)Crafts.Client.Model.Tier.LEGENDARY;
                            Console.WriteLine($"kat upgrade cost {(Tier)rarityInt}({rarityInt}) {cost?.TargetRarity} {sell.Tier}");
                            var raw = rawCost.Where(c => ((int)c.BaseRarity) == rarityInt && sell.Tag.EndsWith(c.Name.Replace(' ', '_').ToUpper())).FirstOrDefault();
                            if (i == 5 && sell.Tag == "PET_JERRY")
                            {
                                yield return await CostOf("PET_ITEM_TOY_JERRY", "Jerry 3d glasses");
                                break;
                            }
                            if (raw == null)
                                throw new Exception($"could not find kat cost for tier {i}({(Tier)rarityInt}) and tag {sell.Tag} {buy.Uuid} -> {sell.Uuid}");
                            upgradeCost = raw.Cost * (1.0 - 0.003 * level);
                            if (raw.Material != null)
                            {
                                costAdded = true;
                                yield return await CostOf(raw.Material, materialTitle, raw.Amount);
                            }
                        }
                        yield return new($"Kat cost for {tierName}", (long)-upgradeCost);
                        if (cost?.MaterialCost > 0 && !costAdded)
                            yield return new(materialTitle, (long)-cost.MaterialCost);
                        if (i == (int)Tier.LEGENDARY)
                            break;
                    }
                }
            }
            else if (sell.FlatenedNBT.Where(l => l.Key == "rarity_upgrades").Any() && !buy.FlatNbt.Where(l => l.Key == "rarity_upgrades").Any())
                yield return await CostOf("RECOMBOBULATOR_3000", "Recombobulator");
            else
                logger.LogWarning($"could not find rarity change source for {sell.Tag} {buy.Uuid} -> {sell.Uuid}");
        // determine gem differences 
        var gemsOnPurchase = buy.FlatNbt.Where(f => f.Value == "PERFECT" || f.Value == "FLAWLESS").ToList();
        var gemsOnSell = sell.FlatenedNBT.Where(f => f.Value == "PERFECT" || f.Value == "FLAWLESS").ToList();
        var gemsAdded = gemsOnSell.Except(gemsOnPurchase).ToList();
        var gemsRemoved = gemsOnPurchase.Except(gemsOnSell).ToList();
        foreach (var gem in gemsAdded)
        {
            string type = GetCorrectKey(gem, sell.FlatenedNBT);
            yield return await CostOf($"{gem.Value}_{type}_GEM", $"{gem.Value} {type} gem added");
        }
        foreach (var gem in gemsRemoved)
        {
            string type = GetCorrectKey(gem, buy.FlatNbt);
            yield return await ValueOf($"{gem.Value}_{type}_GEM", $"{gem.Value} {type} gem removed");
        }

        var itemsOnPurchase = buy.FlatNbt.Where(f => ItemKeys.Contains(f.Key)).ToList();
        var itemsOnSell = sell.FlatenedNBT.Where(f => ItemKeys.Contains(f.Key)).ToList();
        var itemsAdded = itemsOnSell.Except(itemsOnPurchase).ToList();
        var itemsRemoved = itemsOnPurchase.Except(itemsOnSell).ToList();
        foreach (var item in itemsAdded)
        {
            yield return await CostOf(item.Value, $"{item.Value} {item.Key} added");
        }
        foreach (var item in itemsRemoved)
        {
            yield return await ValueOf(item.Value, $"{item.Value} {item.Key} removed");
        }
    }

    private static Crafts.Client.Model.ProfitableCraft AddCraftPathIngredients(string tagOnPurchase, List<Crafts.Client.Model.ProfitableCraft> allCrafts, List<Crafts.Client.Model.Ingredient> allIngredients, int depth = 0)
    {
        if (allIngredients.Where(i => i.ItemId == tagOnPurchase).Any())
            return null;
        if (depth > 10)
            return null;
        // search deeper
        foreach (var item in allIngredients.ToList())
        {
            var subCraft = allCrafts.Where(c => c.ItemId == item.ItemId).FirstOrDefault();
            if (subCraft == null)
                continue;
            if (subCraft.Ingredients.Where(i => i.ItemId == tagOnPurchase).Any())
            {
                allIngredients.AddRange(subCraft.Ingredients);
                return subCraft;
            }
            var foundSubCraft = AddCraftPathIngredients(tagOnPurchase, allCrafts, subCraft.Ingredients, depth + 1);
            if (foundSubCraft != null)
            {
                allIngredients.AddRange(subCraft.Ingredients.Where(i => i.ItemId != foundSubCraft.ItemId && i.ItemId != subCraft.ItemId));
                allIngredients.Remove(item);
                return subCraft;
            }
        }
        return null;
    }

    private static string GetCorrectKey(KeyValuePair<string, string> gem, Dictionary<string, string> flat)
    {
        var type = gem.Key.Split("_")[0];
        if (type == "UNIVERSAL" || type == "COMBAT" || type == "DEFENSIVE" || type == "MINING")
            type = flat.Where(f => f.Key == gem.Key + "_gem").FirstOrDefault().Value;
        return type;
    }

    private static bool IsNotcaluclateable(Core.SaveAuction sell)
    {
        return sell.Tag?.EndsWith("_GIFT_TALISMAN") ?? true;
    }

    private async Task<Items.Client.Model.Item> GetItemMetadata(string tag)
    {
        var itemMetadata = await itemApi.ItemItemTagGetAsync(tag, true);
        if (itemMetadata == null)
            throw new Exception($"could not find item metadata for {tag}");
        return itemMetadata;
    }

    private async Task<PastFlip.ProfitChange> CostOf(string item, string title, int amount = 1)
    {
        if (item == "MOVE_JERRY")
            return new PastFlip.ProfitChange()
            {
                Label = title,
                Amount = -1
            };
        var median = (await pricesApi.ApiItemPriceItemTagGetAsync(item)
                    ?? throw new Exception($"Failed to find price for {item}")).Median;
        if (median >= int.MaxValue)
            median = 0;
        return new PastFlip.ProfitChange()
        {
            Label = title,
            Amount = -(long)median * amount
        };
    }

    private async Task<PastFlip.ProfitChange> ValueOf(string item, string title, int amount = 1)
    {
        return new PastFlip.ProfitChange()
        {
            Label = title,
            Amount = (long)(await pricesApi.ApiItemPriceItemTagGetAsync(item)
                ?? throw new Exception($"could not find price for {item}")).Median * amount * 98 / 100
        };
    }
}