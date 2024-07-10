using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.Api.Client.Model;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Items.Client.Api;
using System.Text.RegularExpressions;
using Coflnet.Sky.Core;
using System.Runtime.Serialization;
using Coflnet.Sky.Core.Services;
using System.Security.Cryptography;

namespace Coflnet.Sky.SkyAuctionTracker.Services;


/// <summary>
/// Organizses any and every profit changes
/// </summary>
public class ProfitChangeService
{
    private const int ExpPetMaxLevel = 25353230;
    private const int ExpMaxLevelGoldenDragon = 210255385;
    private Coflnet.Sky.Api.Client.Api.IPricesApi pricesApi;
    private Crafts.Client.Api.IKatApi katApi;
    private ICraftsApi craftsApi;
    private IItemsApi itemApi;
    private readonly ILogger<ProfitChangeService> logger;
    private Core.PropertyMapper mapper = new();
    private Bazaar.Client.Api.IBazaarApi bazaarApi;
    private HypixelItemService hypixelItemService;
    private IPriceProviderFactory priceProviderFactory;
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
    /// <param name="bazaarApi"></param>
    /// <param name="priceProviderFactory"></param>
    public ProfitChangeService(
        Coflnet.Sky.Api.Client.Api.IPricesApi pricesApi,
        Crafts.Client.Api.IKatApi katApi,
        ICraftsApi craftsApi,
        ILogger<ProfitChangeService> logger,
        IItemsApi itemApi,
        HypixelItemService hypixelItemService,
        Bazaar.Client.Api.IBazaarApi bazaarApi,
        IPriceProviderFactory priceProviderFactory)
    {
        this.pricesApi = pricesApi;
        this.katApi = katApi;
        this.craftsApi = craftsApi;
        this.logger = logger;
        this.itemApi = itemApi;
        this.hypixelItemService = hypixelItemService;
        this.bazaarApi = bazaarApi;
        this.priceProviderFactory = priceProviderFactory;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="buy"></param>
    /// <param name="sell"></param>
    /// <returns></returns>
    public async IAsyncEnumerable<PastFlip.ProfitChange> GetChanges(Coflnet.Sky.Core.SaveAuction buy, Coflnet.Sky.Core.SaveAuction sell)
    {
        yield return GetAhTax(sell.HighestBidAmount, sell.StartingBid);
        var priceProvider = priceProviderFactory.Create(sell);
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
            await foreach (var item in GetCraftCosts(buy, sell, priceProvider))
            {
                yield return item;
            }
        }

        if (buy.Tier == Core.Tier.UNKNOWN && buy.Tag.StartsWith("PET_"))
        {
            if (Enum.TryParse(buy.FlatenedNBT.Where(l => l.Key == "tier").FirstOrDefault().Value, out Core.Tier tier))
                buy.Tier = tier;
            logger.LogInformation($"upgraded rarity to {buy.Tier} of {buy.Uuid} due to pet tier");
        }
        var targetTier = sell.Tier;
        if (buy.FlatenedNBT.Any(f => f.Key == "heldItem" && f.Value == "PET_ITEM_TIER_BOOST")
         && !sell.FlatenedNBT.Any(f => f.Key == "heldItem" && f.Value == "PET_ITEM_TIER_BOOST"))
        {
            buy.Tier--;
        }
        if ((int)buy.Tier < (int)sell.Tier)
            if (sell.Tag.StartsWith("PET_"))
            {
                await foreach (var item in GetPetRarityUpgrades(buy, sell, priceProvider))
                {
                    yield return item;
                }
            }
            else
            {
                var isrecombobulated = sell.FlatenedNBT.Where(l => l.Key == "rarity_upgrades").Any();
                var wasRecombobulated = buy.FlatenedNBT.Where(l => l.Key == "rarity_upgrades").Any();
                if (isrecombobulated && !wasRecombobulated)
                {
                    yield return await priceProvider.CostOf("RECOMBOBULATOR_3000", "Recombobulator");
                    targetTier--;
                }
                if (sell.Tag == "PULSE_RING")
                {
                    var currentCharge = sell.FlatenedNBT.FirstOrDefault(f => f.Key == "thunder_charge").Value ?? "0";
                    if (wasRecombobulated)
                        targetTier--;
                    var toibCount = (int.Parse(currentCharge) - buy.GetNbtValue("thunder_charge")) / 50_000;
                    yield return await priceProvider.CostOf("THUNDER_IN_A_BOTTLE", $"{toibCount}x Thunder in a bottle", toibCount);
                    targetTier = buy.Tier; // handled conversion, don't log
                }
                if ((int)buy.Tier != (int)targetTier)
                    logger.LogWarning($"could not find rarity change source for {sell.Tag} {buy.Uuid} -> {sell.Uuid}");
            }
        var gemsOnPurchase = GetGems(buy);
        var gemsOnSell = GetGems(sell);
        List<string> gemsAdded = GemsMissingFromFirstInSecond(gemsOnPurchase, gemsOnSell);
        var gemsRemoved = GemsMissingFromFirstInSecond(gemsOnSell, gemsOnPurchase);
        foreach (var itemKey in gemsAdded)
        {
            var parts = itemKey.Split('_');
            yield return await priceProvider.CostOf(itemKey, $"{parts[0]} {parts[1]} gem added");
        }
        foreach (var itemKey in gemsRemoved)
        {
            var parts = itemKey.Split('_');
            var rarity = parts[0];
            var gemValue = await ValueOf(itemKey, $"{rarity} {parts[1]} gem removed");
            gemValue.Amount -= rarity switch
            {
                "PERFECT" => 500_000,
                "FLAWLESS" => 100_000,
                "FINE" => 10_000,
                "FLAWED" => 100,
                _ => 0
            };
            yield return gemValue;
        }

        var itemsOnPurchase = buy.FlatenedNBT.Where(f => ItemKeys.Contains(f.Key)).ToList();
        var itemsOnSell = sell.FlatenedNBT.Where(f => ItemKeys.Contains(f.Key)).ToList();
        var itemsAdded = itemsOnSell.Except(itemsOnPurchase).ToList();
        var itemsRemoved = itemsOnPurchase.Except(itemsOnSell).ToList();
        foreach (var item in itemsAdded)
        {
            yield return await priceProvider.CostOf(item.Value, $"{item.Value} {item.Key} added");
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
                PastFlip.ProfitChange found = await GetCostForEnchant(item, buy, sell);
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
                var itemCost = await priceProvider.CostOf(reforgeItem.Item1, $"Reforge {sell.Reforge} added");
                itemCost.Amount -= reforgeItem.Item2;
                yield return itemCost;
            }
        }
        foreach (var item in sell.FlatenedNBT.Where(s => !buy.FlatenedNBT.Any(b => b.Key == s.Key && b.Value == s.Value)))
        {
            if (ItemKeys.Contains(item.Key))
                continue; // already handled
            await foreach (var res in GetRemainingDifference(buy, sell, item, priceProvider))
            {
                yield return res;
            }
        }

        List<string> GetGems(Core.SaveAuction buy)
        {
            // determine gem differences 
            return buy.FlatenedNBT.Where(f => f.Value == "PERFECT" || f.Value == "FLAWLESS").Select(f =>
                this.mapper.GetItemKeyForGem(f, buy.FlatenedNBT)
            ).ToList();
        }

        static List<string> GemsMissingFromFirstInSecond(List<string> gemsOnPurchase, List<string> gemsOnSell)
        {
            // remove each occurance only once from the list
            var newList = new List<string>(gemsOnSell);
            foreach (var gem in gemsOnPurchase)
            {
                newList.Remove(gem);
            }
            return newList;
        }
    }

    public PastFlip.ProfitChange GetAhTax(long highestBid, long startingBid = 0)
    {
        var listCostFactor = 1f;
        if (startingBid == 0)
            startingBid = highestBid;
        if (startingBid > 10_000_000)
            listCostFactor = 2;
        if (startingBid >= 100_000_000)
            listCostFactor = 2.5f;
        var ahTax = new PastFlip.ProfitChange()
        {
            Amount = (long)-(
                startingBid * listCostFactor / 100 // listing fee
                + (highestBid > 1_000_000 ? highestBid * 0.01 : 0) // claiming fee
                + 1200 // time fee
                ),
            Label = "ah tax"
        };
        return ahTax;
    }

    private async IAsyncEnumerable<PastFlip.ProfitChange> GetCraftCosts(Core.SaveAuction buy, Core.SaveAuction sell, IPriceProvider priceProvider)
    {
        var tagOnPurchase = buy.Tag;
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
            if (item.ItemId == "SKYBLOCK_COIN")
                yield return new PastFlip.ProfitChange("Coins", -(long)item.Cost);
            if (item.ItemId == "SKYBLOCK_CHOCOLATE")
            {
                var chocolateStickCost = await priceProvider.CostOf("NIBBLE_CHOCOLATE_STICK", "Chocolate stick");
                yield return new PastFlip.ProfitChange($"{count} Chocolate", chocolateStickCost.Amount * count / 250_000_000);
                continue;
            }
            PastFlip.ProfitChange change = null;
            try
            {
                if (count > 0)
                    change = await priceProvider.CostOf(item.ItemId, $"crafting material {item.ItemId}" + (count > 1 ? $" x{count}" : ""), count);
            }
            catch (System.Exception e)
            {
                logger.LogError(e, $"could not find craft cost for {item.ItemId}");
            }
            if (change != null)
                yield return change;
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

    private async IAsyncEnumerable<PastFlip.ProfitChange> GetRemainingDifference(Core.SaveAuction buy, Core.SaveAuction sell, KeyValuePair<string, string> item, IPriceProvider priceProvider)
    {
        if (item.Key == "rarity_upgrades")
            yield break;
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
                    yield return await priceProvider.CostOf(cost.ItemId, $"Slot unlock item {cost.ItemId}x{cost.Amount}", cost.Amount ?? 1);
            }
        }
        if (item.Key == "upgrade_level")
        {
            var baseLevel = int.Parse(valueOnBuy.Value ?? buy.FlatenedNBT.GetValueOrDefault("dungeon_item_level") ?? "0");
            var upgradeCost = await hypixelItemService.GetStarCost(sell.Tag, baseLevel, int.Parse(item.Value));
            foreach (var cost in upgradeCost)
            {
                if (cost.Type == "ESSENCE")
                    yield return await priceProvider.CostOf($"ESSENCE_{cost.EssenceType}", $"{cost.EssenceType} essence x{cost.Amount} to add star", cost.Amount);
                else if (cost.Type == "ITEM")
                    yield return await priceProvider.CostOf(cost.ItemId, $"{cost.ItemId}x{cost.Amount} for star", cost.Amount);
            }
        }
        if (item.Key == "exp")
        {
            var endLevel = "100";
            var maxExpForPet = ExpPetMaxLevel;
            if (sell.Tag == "PET_GOLDEN_DRAGON")
            {
                endLevel = "200";
                maxExpForPet = ExpMaxLevelGoldenDragon;
            }
            var level1Cost = await pricesApi.ApiItemPriceItemTagGetAsync(sell.Tag, new() { { "PetLevel", "1" }, { "Rarity", "LEGENDARY" } });
            var level100Cost = await pricesApi.ApiItemPriceItemTagGetAsync(sell.Tag,
                    new() { { "PetLevel", endLevel }, { "Rarity", "LEGENDARY" }, { "PetItem", "NOT_TIER_BOOST" } }) ?? new();
            var perExpCost = (float)(level100Cost.Median - level1Cost.Median) / maxExpForPet;
            if (sell.Tag == "PET_SUBZERO_WISP")
            {
                // can get exp with hypergolic gabagool
                var gabagoolCost = await pricesApi.ApiItemPriceItemTagGetAsync("HYPERGOLIC_GABAGOOL");
                perExpCost = Math.Min(perExpCost, gabagoolCost.Median / 3276800);
            }
            var currentExp = ParseFloat(item.Value);
            float addedExp = Math.Min(currentExp, maxExpForPet) - ParseFloat(valueOnBuy.Value ?? "0");
            if (addedExp > 0)
                yield return new PastFlip.ProfitChange()
                {
                    Label = $"Exp cost for {item.Value} exp",
                    Amount = -(long)(perExpCost * addedExp)
                };
        }
        if (Constants.AttributeKeys.Contains(item.Key))
        {
            var baseLevel = ParseFloat(valueOnBuy.Value ?? "0");
            if (baseLevel == 0) // wheel of fate applied
            {
                var previousAttributes = buy.FlatenedNBT.Where(f => Constants.AttributeKeys.Contains(f.Key)).ToList();
                var currentAttri = sell.FlatenedNBT.Where(f => Constants.AttributeKeys.Contains(f.Key)).ToList();
                if (currentAttri.First().Key == item.Key)
                {
                    // add only for first attribute
                    yield return await priceProvider.CostOf("WHEEL_OF_FATE", "Wheel of fate cost");
                }
                var val = previousAttributes.Select(f => (f, ParseFloat(f.Value), diff: Math.Abs(ParseFloat(f.Value) - ParseFloat(item.Value))))
                        .OrderBy(f => f.diff).First();
                Console.WriteLine($"base level {val} for {baseLevel}");
                if (val.diff == 0)
                    yield break;
                baseLevel = ParseFloat(val.f.Value);
            }
            var sellLevel = ParseFloat(item.Value);
            var difference = sellLevel - baseLevel;
            var basedOnLvl2 = 2;
            var attributeShardCost = await pricesApi.ApiItemPriceItemTagGetAsync("ATTRIBUTE_SHARD", new() { { item.Key, basedOnLvl2.ToString() } });
            var costOfLvl2 = await pricesApi.ApiItemPriceItemTagGetAsync(sell.Tag, new() { { item.Key, basedOnLvl2.ToString() } });
            var target = Math.Min((attributeShardCost?.Median == 0) ? 2_000_000 : attributeShardCost.Median,
                                costOfLvl2?.Median ?? attributeShardCost?.Median ?? 2_000_000);
            if (target == 0)
            {
                logger.LogInformation($"could not find attribute cost for {item.Key} lvl {basedOnLvl2} on {sell.Tag}");
                yield break;
            }
            var sellValue = Math.Pow(2, sellLevel - basedOnLvl2) * target;
            if (sellLevel > 5)
            {
                // check for higher level
                var costOfLvl5 = await pricesApi.ApiItemPriceItemTagGetAsync(sell.Tag, new() { { item.Key, "5" } });
                var needed = Math.Pow(2, sellLevel - 5);
                sellValue = Math.Min(needed * (costOfLvl5?.Median ?? int.MaxValue), sellValue);
            }
            var buyValue = Math.Pow(2, baseLevel - basedOnLvl2) * target;

            yield return new PastFlip.ProfitChange()
            {
                Label = $"Cost for {item.Key} lvl {item.Value}",
                Amount = -(long)(sellValue - buyValue)
            };

        }
        if (item.Key == "additional_coins")
        {
            var coins = ParseFloat(item.Value);
            var previous = ParseFloat(valueOnBuy.Value ?? "0");
            var sum = -(long)(coins - previous);
            if (sum != 0)
            {
                yield return new PastFlip.ProfitChange()
                {
                    Label = "Additional coins",
                    Amount = sum
                };
                yield return await priceProvider.CostOf("STOCK_OF_STONKS", "Stock of Stonks", 3);
            }
        }
        // missing nbt
        if (!mapper.TryGetIngredients(item.Key, item.Value, valueOnBuy.Value, out var items))
            yield break;

        foreach (var ingredient in items.GroupBy(i => i).Select(g => (g.Key, count: g.Count())))
        {
            if (item.Value == "PET_ITEM_TIER_BOOST")
                continue; // already handled
            if (item.Key.StartsWith("RUNE_") && item.Key != ingredient.Key)
                continue; // rune mapping returns both without and with level and here we only handle without
            if (item.Key == "ability_scroll")
            {
                yield return await priceProvider.CostOf(ingredient.Key, $"Applied {ingredient.Key}");
                continue;
            }
            if (item.Key == "skin" && sell.Tag.StartsWith("PET_"))
            {
                yield return await priceProvider.CostOf($"PET_SKIN_" + ingredient.Key, $"Applied {ingredient.Key}");
                continue;
            }
            if (ingredient.count == 1)
                yield return await priceProvider.CostOf(ingredient.Key, $"Used {ingredient.Key} to upgraded {item.Key} to {item.Value}", ingredient.count);
            else
                yield return await priceProvider.CostOf(ingredient.Key, $"Used {ingredient.count}x {ingredient.Key} to upgraded {item.Key} to {item.Value}", ingredient.count);
        }
    }

    private async IAsyncEnumerable<PastFlip.ProfitChange> GetPetRarityUpgrades(Core.SaveAuction buy, Core.SaveAuction sell, IPriceProvider priceProvider)
    {
        var sellTier = sell.Tier;
        if (sell.FlatenedNBT.Where(l => l.Key == "heldItem" && l.Value == "PET_ITEM_TIER_BOOST").Any())
        {
            yield return await priceProvider.CostOf("PET_ITEM_TIER_BOOST", "tier Boost cost");
            if (buy.Tier >= sell.Tier - 1 || buy.Tier == Core.Tier.LEGENDARY)
                yield break;
            sellTier--;
        }
        Console.WriteLine($"buy tier {(int)buy.Tier} {buy.Tier} sell tier {(int)sellTier} {sellTier}");
        for (int i = ((int)buy.Tier); i < (int)sellTier; i++)
        {
            var allCosts = await katApi.KatAllGetAsync(0, default);
            if (allCosts == null)
                throw new Exception("could not get kat costs from crafts api");
            var cost = allCosts.Where(c => ((int)c.TargetRarity) > i && c.CoreData.ItemTag == sell.Tag)
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
            if (cost == null || cost.MaterialCost >= 1_000_000 || level > 2)
            {
                // approximate cost with raw
                var rawCost = await katApi.KatRawGetAsync();
                var rarityInt = i;
                if (i > (int)Core.Tier.LEGENDARY)
                    break;
                Console.WriteLine($"kat upgrade cost {(Core.Tier)rarityInt}({rarityInt}) {cost?.TargetRarity} {sell.Tier}");
                var raw = rawCost.Where(c => ((int)c.BaseRarity) == rarityInt && sell.Tag.EndsWith(c.Name.Replace(' ', '_').ToUpper())).FirstOrDefault();
                if (i == 5 && sell.Tag == "PET_JERRY")
                {
                    yield return await priceProvider.CostOf("PET_ITEM_TOY_JERRY", "Jerry 3d glasses");
                    break;
                }
                // special pet upgrades 
                if (sell.Tag.EndsWith("_WISP"))
                {
                    var allCrafts = await craftsApi.CraftsAllGetAsync();
                    var kind = sell.Tag.Split('_')[1];
                    var craft = allCrafts.Where(c => c.ItemId == $"UPGRADE_STONE_{kind}").FirstOrDefault();
                    if (craft == null)
                        throw new Exception($"could not find craft for wisp UPGRADE_STONE_{kind}");
                    yield return new PastFlip.ProfitChange()
                    {
                        Label = $"Wisp upgrade stone for {kind}",
                        Amount = (long)-craft.CraftCost
                    };
                    break;
                }
                if (raw == null)
                    throw new Exception($"could not find kat cost for tier {i}({(Core.Tier)rarityInt}) and tag {sell.Tag} {buy.Uuid} -> {sell.Uuid}");
                upgradeCost = raw.Cost * (1.0 - 0.003 * level);
                if (raw.Material != null)
                {
                    costAdded = true;
                    yield return await priceProvider.CostOf(raw.Material, materialTitle, raw.Amount);
                }
            }
            yield return new($"Kat cost for {tierName}", (long)-upgradeCost);
            if (cost?.MaterialCost > 0 && !costAdded)
                yield return new(materialTitle, (long)-cost.MaterialCost);
            if (i == (int)Core.Tier.LEGENDARY)
                break;
        }

    }

    private float ParseFloat(string value)
    {
        return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<PastFlip.ProfitChange> GetCostForEnchant(Core.Enchantment item, Core.SaveAuction buy, Core.SaveAuction sell)
    {
        if (item.Type == Core.Enchantment.EnchantmentType.telekinesis)
            return null; // not a book anymore
        if (sell.Tag == "PROMISING_SPADE" && item.Type == Core.Enchantment.EnchantmentType.efficiency)
        {
            var difference = buy.GetNbtValue("blocksBroken") - Math.Min(sell.GetNbtValue("blocksBroken"), 20_000);
            return new PastFlip.ProfitChange()
            {
                Label = $"Mined {difference} blocks",
                Amount = difference
            };
        }
        PastFlip.ProfitChange found = null;
        try
        {
            var allBazaar = await bazaarApi.ApiBazaarPricesGetAsync();
            var itemValues = allBazaar.ToDictionary(b => b.ProductId, b => b.SellPrice);
            var sellValue = mapper.EnchantValue(item, sell.FlatenedNBT, itemValues);
            var buyValue = 0L;
            var enchantAtBuy = buy.Enchantments.Where(e => e.Type == item.Type).FirstOrDefault();
            if (enchantAtBuy != default && (enchantAtBuy.Level != item.Level - 1 //&& item.Level < 7
                || Constants.EnchantToAttribute.ContainsKey(item.Type)))
            {
                buyValue = mapper.EnchantValue(enchantAtBuy, buy.FlatenedNBT, itemValues);
                found = new PastFlip.ProfitChange()
                {
                    Label = $"Enchant {item.Type} from {enchantAtBuy.Level} to {item.Level}",
                    Amount = buyValue - sellValue
                };
            }
            else if (enchantAtBuy != default && enchantAtBuy.Level == item.Level - 1 && IsProbablyCombinable(item))
            {
                // only requires another book of the same level
                var enchantDummy = new Core.Enchantment()
                {
                    Type = item.Type,
                    Level = (byte)(item.Level - 1)
                };
                return new PastFlip.ProfitChange()
                {
                    Label = $"Enchant {item.Type} {item.Level} added",
                    Amount = -mapper.EnchantValue(enchantDummy, buy.FlatenedNBT, itemValues)
                };
            }
            else
            {
                if (sellValue == -1)
                    return null; // enchant not on bazaar, ignore
                return new PastFlip.ProfitChange()
                {
                    Label = $"Enchant {item.Type} {item.Level}",
                    Amount = -sellValue
                };
            }
        }
        catch (System.Exception e)
        {
            logger.LogError(e, $"could not find enchant cost for {item.Type}");
        }

        return found;

        static bool IsProbablyCombinable(Core.Enchantment item)
        {
            return item.Level < 6 && (!Constants.VeryValuableEnchant.TryGetValue(item.Type, out var value) || value < item.Level);
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
                var toAdd = subCraft.Ingredients.Where(i => i.ItemId != subCraft.ItemId);
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
        return mapper.GetItemKeyForGem(gem, flat);
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

    private async Task<PastFlip.ProfitChange> CostOf(string item, string title, long amount = 1)
    {
        if (item == "MOVE_JERRY")
            return new PastFlip.ProfitChange()
            {
                Label = title,
                Amount = -1
            };
        if (item == "SKYBLOCK_COIN")
            return new PastFlip.ProfitChange()
            {
                Label = title,
                Amount = -amount
            };

        var itemPrice = await pricesApi.ApiItemPriceItemTagGetAsync(item)
                    ?? throw new Exception($"Failed to find price for {item}");
        var median = itemPrice.Median;
        if (itemPrice.Max == 0 && item.StartsWith("ENCHANTMENT"))
        {
            // get lvl 1 and scale up, sample ENCHANTMENT_ULTIMATE_CHIMERA_4
            var lvl1Price = await pricesApi.ApiItemPriceItemTagGetAsync(item.Substring(0, item.LastIndexOf('_') + 1) + "1");
            median = lvl1Price.Median * int.Parse(item.Substring(item.LastIndexOf('_') + 1));
        }
        else if (itemPrice.Median == 500_000_000)
        {
            var allCrafts = await craftsApi.CraftsAllGetAsync();
            median = (long)(allCrafts.Where(c => c.ItemId == item).FirstOrDefault()?.CraftCost ?? 500_000_000);
        }
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

public static class AuctionShortcuts
{
    public static int GetNbtValue(this Core.SaveAuction auction, string key)
    {
        return int.Parse(auction.FlatenedNBT.Where(f => f.Key == key).FirstOrDefault().Value ?? "0");
    }
}