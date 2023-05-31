using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.Api.Client.Model;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Items.Client.Api;
using System.Text.RegularExpressions;
using Coflnet.Sky.Core;
using System.Runtime.Serialization;

namespace Coflnet.Sky.SkyAuctionTracker.Services;
/// <summary>
/// Organizses any and every profit changes
/// </summary>
public class ProfitChangeService
{
    private const int ExpPetMaxLevel = 25353230;
    private Coflnet.Sky.Api.Client.Api.IPricesApi pricesApi;
    private Crafts.Client.Api.IKatApi katApi;
    private ICraftsApi craftsApi;
    private IItemsApi itemApi;
    private readonly ILogger<ProfitChangeService> logger;
    private Core.PropertyMapper mapper = new();
    private HypixelItemService hypixelItemService;
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
    /// <param name="hypixelItemService"></param>
    public ProfitChangeService(
        Coflnet.Sky.Api.Client.Api.IPricesApi pricesApi,
        Crafts.Client.Api.IKatApi katApi,
        ICraftsApi craftsApi,
        ILogger<ProfitChangeService> logger,
        IItemsApi itemApi,
        HypixelItemService hypixelItemService)
    {
        this.pricesApi = pricesApi;
        this.katApi = katApi;
        this.craftsApi = craftsApi;
        this.logger = logger;
        this.itemApi = itemApi;
        this.hypixelItemService = hypixelItemService;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="buy"></param>
    /// <param name="sell"></param>
    /// <returns></returns>
    public async IAsyncEnumerable<PastFlip.ProfitChange> GetChanges(Coflnet.Sky.Core.SaveAuction buy, Coflnet.Sky.Core.SaveAuction sell)
    {
        var changes = new List<PastFlip.ProfitChange>();
        var listCostFactor = 1f;
        if (sell.HighestBidAmount > 10_000_000)
            listCostFactor = 2;
        if (sell.HighestBidAmount > 100_000_000)
            listCostFactor = 2.5f;
        yield return new PastFlip.ProfitChange()
        {
            Amount = (long)-(
                sell.HighestBidAmount * listCostFactor / 100 // listing fee
                + (sell.HighestBidAmount > 1_000_000 ? sell.HighestBidAmount * 0.01 : 0) // claiming fee
                + 1200 // time fee
                ),
            Label = "ah tax"
        };
        if (IsNotcaluclateable(sell))
            yield break;
        if (buy.Tier == Core.Tier.UNKNOWN)
        {
            var itemMetadata = await GetItemMetadata(buy.Tag);
            buy.Tier = (Core.Tier)itemMetadata.Tier;
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
            foreach (var item in allIngredients)
            {
                var count = item.Count;
                if (item.ItemId == tagOnPurchase)
                    count--;
                if (count > 0)
                    yield return await CostOf(item.ItemId, $"crafting material {item.ItemId}" + (count > 1 ? $" x{count}" : ""), count);
            }
            var itemMetadata = await GetItemMetadata(sell.Tag);
            if ((int)buy.Tier < (int)sell.Tier)
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
            var scrollsOnPurchase = buy.FlatenedNBT.Where(l => l.Key == "ability_scroll").SelectMany(l => l.Value.Split(' ')).ToList();
            var scrollsOnSell = sell.FlatenedNBT.Where(l => l.Key == "ability_scroll").SelectMany(l => l.Value.Split(' ')).ToList();
            var scrollsAdded = scrollsOnSell.Except(scrollsOnPurchase).ToList();
            foreach (var item in scrollsAdded)
            {
                yield return await CostOf(item, $"ability scroll {item}");
            }
        }

        if (buy.Tier == Core.Tier.UNKNOWN && buy.Tag.StartsWith("PET_"))
        {
            if (Enum.TryParse(buy.FlatenedNBT.Where(l => l.Key == "tier").FirstOrDefault().Value, out Core.Tier tier))
                buy.Tier = tier;
            logger.LogInformation($"upgraded rarity to {buy.Tier} of {buy.Uuid} due to pet tier");
        }
        var targetTier = sell.Tier;
        if ((int)buy.Tier < (int)sell.Tier)
            if (sell.Tag.StartsWith("PET_"))
            {
                if (sell.FlatenedNBT.Where(l => l.Key == "heldItem" && l.Value == "PET_ITEM_TIER_BOOST").Any())
                    yield return await CostOf("PET_ITEM_TIER_BOOST", "tier Boost cost");
                else
                {
                    Console.WriteLine($"buy tier {(int)buy.Tier} {buy.Tier} sell tier {(int)sell.Tier} {sell.Tier}");
                    for (int i = ((int)buy.Tier); i < (int)sell.Tier; i++)
                    {
                        var allCosts = await katApi.KatAllGetAsync(0, default);
                        if (allCosts == null)
                            throw new Exception("could not get kat costs from crafts api");
                        var cost = allCosts.Where(c => ((int)c.TargetRarity) > i + 1 && c.CoreData.ItemTag == sell.Tag)
                                    .OrderBy(c => c.TargetRarity).FirstOrDefault();
                        var upgradeCost = cost?.UpgradeCost;
                        var tierName = (i >= (int)Core.Tier.LEGENDARY) ? sell.Tier.ToString() : ((Core.Tier)i + 1).ToString();
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
                            if (i > (int)Core.Tier.LEGENDARY)
                                break;
                            //  rarityInt = (int)Crafts.Client.Model.Tier.LEGENDARY;
                            Console.WriteLine($"kat upgrade cost {(Core.Tier)rarityInt}({rarityInt}) {cost?.TargetRarity} {sell.Tier}");
                            var raw = rawCost.Where(c => ((int)c.BaseRarity) == rarityInt && sell.Tag.EndsWith(c.Name.Replace(' ', '_').ToUpper())).FirstOrDefault();
                            if (i == 5 && sell.Tag == "PET_JERRY")
                            {
                                yield return await CostOf("PET_ITEM_TOY_JERRY", "Jerry 3d glasses");
                                break;
                            }
                            if (raw == null)
                                throw new Exception($"could not find kat cost for tier {i}({(Core.Tier)rarityInt}) and tag {sell.Tag} {buy.Uuid} -> {sell.Uuid}");
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
                        if (i == (int)Core.Tier.LEGENDARY)
                            break;
                    }
                }
            }
            else
            {
                if (sell.FlatenedNBT.Where(l => l.Key == "rarity_upgrades").Any() && !buy.FlatenedNBT.Where(l => l.Key == "rarity_upgrades").Any())
                {
                    yield return await CostOf("RECOMBOBULATOR_3000", "Recombobulator");
                    targetTier--;
                }
                if (sell.Tag == "PULSE_RING")
                {
                    var toibCount = 0;
                    if (targetTier >= Core.Tier.LEGENDARY)
                        toibCount += 80;
                    if (buy.Tier <= Core.Tier.UNCOMMON)
                        toibCount += 3;
                    if (buy.Tier < Core.Tier.EPIC && targetTier >= Core.Tier.EPIC)
                        toibCount += 17;

                    yield return await CostOf("THUNDER_IN_A_BOTTLE", $"{toibCount}x Thunder in a bottle", toibCount);
                    targetTier--;
                }
                if ((int)buy.Tier != (int)targetTier)
                    logger.LogWarning($"could not find rarity change source for {sell.Tag} {buy.Uuid} -> {sell.Uuid}");
            }
        // determine gem differences 
        var gemsOnPurchase = buy.FlatenedNBT.Where(f => f.Value == "PERFECT" || f.Value == "FLAWLESS").ToList();
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
            string type = GetCorrectKey(gem, buy.FlatenedNBT);
            yield return await ValueOf($"{gem.Value}_{type}_GEM", $"{gem.Value} {type} gem removed");
        }

        var itemsOnPurchase = buy.FlatenedNBT.Where(f => ItemKeys.Contains(f.Key)).ToList();
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
        var newEnchantmens = sell.Enchantments?.Where(f => !buy.Enchantments?.Where(e => e.Type == f.Type && f.Level == e.Level).Any() ?? true).ToList();
        if (newEnchantmens != null)
            foreach (var item in newEnchantmens)
            {
                if (buy.Enchantments.Any(e => e.Type == Core.Enchantment.EnchantmentType.unknown && e.Level == item.Level))
                    continue; // skip unkown enchants that would match
                PastFlip.ProfitChange found = await GetCostForEnchant(item, buy.Enchantments);
                if (found != null)
                    yield return found;
            }
        var reforgeName = sell.Reforge.ToString().ToLower().Replace("_", "");
        if (sell.Reforge == Core.ItemReferences.Reforge.warped_on_aote)
        {
            // special case for alias
            reforgeName = "aotestone";
        }
        if (buy.Reforge != sell.Reforge)
        {
            var reforgeItem = mapper.GetReforgeCost(sell.Reforge, sell.Tier);
            if (reforgeItem.Item1 != string.Empty)
            {
                var itemCost = await CostOf(reforgeItem.Item1, $"Reforge {sell.Reforge} added");
                itemCost.Amount -= reforgeItem.Item2;
                yield return itemCost;
            }
        }
        foreach (var item in sell.FlatenedNBT.Where(s => !buy.FlatenedNBT.Any(b => b.Key == s.Key && b.Value == s.Value)))
        {
            if (item.Key == "rarity_upgrades")
                continue;
            var valueOnBuy = buy.FlatenedNBT.Where(f => f.Key == item.Key).FirstOrDefault();
            if (item.Key == "unlocked_slots")
            {
                var slotCost = await hypixelItemService.GetSlotCost(
                    sell.Tag,
                    buy.FlatenedNBT.Where(f => f.Key == "unlocked_slots").SelectMany(f => f.Value.Split(',')).ToList(),
                    item.Value.Split(',').ToList());
                foreach (var cost in slotCost)
                {
                    if (cost.Coins != 0)
                        yield return new PastFlip.ProfitChange()
                        {
                            Label = "Slot unlock cost",
                            Amount = -cost.Coins
                        };
                    else if (cost.Type == "ITEM")
                        yield return await CostOf(cost.ItemId, $"Slot unlock item {cost.ItemId}x{cost.Amount}", cost.Amount ?? 1);
                }
            }
            if (item.Key == "upgrade_level")
            {
                var upgradeCost = await hypixelItemService.GetStarCost(sell.Tag, int.Parse(valueOnBuy.Value ?? "0"), int.Parse(item.Value));
                foreach (var cost in upgradeCost)
                {
                    if (cost.Type == "ESSENCE")
                        yield return await CostOf($"ESSENCE_{cost.EssenceType}", $"{cost.EssenceType} essence x{cost.Amount} to add star", cost.Amount);
                    else if (cost.Type == "ITEM")
                        yield return await CostOf(cost.ItemId, $"{cost.ItemId}x{cost.Amount} for star", cost.Amount);
                }
            }
            if (item.Key == "exp")
            {
                var level1Cost = await pricesApi.ApiItemPriceItemTagGetAsync(sell.Tag, new() { { "PetLevel", "1" }, { "Rarity", "LEGENDARY" } });
                var level100Cost = await pricesApi.ApiItemPriceItemTagGetAsync(sell.Tag, new() { { "PetLevel", "100" }, { "Rarity", "LEGENDARY" } });
                var expCost = (float)(level100Cost.Median - level1Cost.Median) / ExpPetMaxLevel;
                var currentExp = ParseFloat(item.Value);
                float addedExp = Math.Min(currentExp, ExpPetMaxLevel) - ParseFloat(valueOnBuy.Value ?? "0");
                if (addedExp > 0)
                    yield return new PastFlip.ProfitChange()
                    {
                        Label = $"Exp cost for {item.Value} exp",
                        Amount = -(long)(expCost * addedExp)
                    };
            }
            // missing nbt
            if (!mapper.TryGetIngredients(item.Key, item.Value, valueOnBuy.Value, out var items))
                continue;

            foreach (var ingredient in items)
            {
                yield return await CostOf(ingredient, $"Used {ingredient} to upgraded {item.Key} to {item.Value}");
            }
        }
    }

    private float ParseFloat(string value)
    {
        return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<PastFlip.ProfitChange> GetCostForEnchant(Core.Enchantment item, List<Core.Enchantment> enchantments)
    {
        if (item.Type == Core.Enchantment.EnchantmentType.telekinesis)
            return null; // not a book anymore
        PastFlip.ProfitChange found = null;
        int requiredLevel = item.Level;
        var matchingEnchant = enchantments.Where(e => e.Type == item.Type).FirstOrDefault();
        if (matchingEnchant != null && matchingEnchant.Level == item.Level - 1)
        {
            // only required one level lower book
            requiredLevel = item.Level - 1;
        }
        try
        {
            found = await CostOf($"ENCHANTMENT_{item.Type}_{requiredLevel}".ToUpper(), $"Enchant {item.Type} lvl {requiredLevel} added");
            if (found.Amount == 0)
                return null; // ignore 0 cost enchants
        }
        catch (System.Exception e)
        {
            logger.LogError(e, $"could not find enchant cost for {item.Type} lvl {requiredLevel}");
        }

        return found;
    }

    private static Crafts.Client.Model.ProfitableCraft AddCraftPathIngredients(string tagOnPurchase, List<Crafts.Client.Model.ProfitableCraft> allCrafts, List<Crafts.Client.Model.Ingredient> allIngredients, int depth = 0)
    {
        Console.WriteLine($"searching for {tagOnPurchase}");
        if (allIngredients.Where(i => i.ItemId == tagOnPurchase).Any())
            return null;
        if (depth > 10)
            return null;
        // search deeper
        foreach (var item in allIngredients.ToList())
        {
            Console.WriteLine($"searching for {item.ItemId} in {tagOnPurchase}");
            var subCraft = allCrafts.Where(c => c.ItemId == item.ItemId).FirstOrDefault();
            if (subCraft == null)
                continue;
            if (subCraft.Ingredients.Where(i => i.ItemId == tagOnPurchase).Any())
            {
                var toAdd = subCraft.Ingredients.Where(i => i.ItemId != tagOnPurchase && i.ItemId != subCraft.ItemId);
                allIngredients.AddRange(toAdd);
                RemoveItem(allIngredients, item);
                return subCraft;
            }
            var foundSubCraft = AddCraftPathIngredients(tagOnPurchase, allCrafts, subCraft.Ingredients, depth + 1);
            if (foundSubCraft != null)
            {
                allIngredients.AddRange(subCraft.Ingredients.Where(i => i.ItemId != foundSubCraft.ItemId && i.ItemId != subCraft.ItemId));
                RemoveItem(allIngredients, item);
                return subCraft;
            }
        }
        return null;

        static void RemoveItem(List<Crafts.Client.Model.Ingredient> allIngredients, Crafts.Client.Model.Ingredient item)
        {
            var matchingIngredient = allIngredients.Where(i => i.ItemId == item.ItemId).FirstOrDefault();
            if (matchingIngredient != null)
                if (matchingIngredient.Count > 1)
                    matchingIngredient.Count--;
                else
                    allIngredients.Remove(matchingIngredient);
        }
    }

    private string GetCorrectKey(KeyValuePair<string, string> gem, Dictionary<string, string> flat)
    {
        return mapper.GetCorrectGemType(gem, flat);
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

public class ApiSaveAuction : Core.SaveAuction
{
    /// <summary>
    /// 
    /// </summary>
    [DataMember(Name = "flatNbt", EmitDefaultValue = true)]
    public override Dictionary<string, string> FlatenedNBT { get; set; }
}
