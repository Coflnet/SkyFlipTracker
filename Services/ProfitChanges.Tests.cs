using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.Api.Client.Model;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Crafts.Client.Model;
using NUnit.Framework;
using Moq;
using Newtonsoft.Json;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class ProfitChangeTests
{
    ProfitChangeService service;


    [Test]
    public async Task EndermanMultiLevel()
    {
        var buy = new ColorSaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_ENDERMAN",
            HighestBidAmount = 1000,
            FlatNbt = new(),
            Tier = Api.Client.Model.Tier.RARE
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_ENDERMAN",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.MYTHIC
        };
        var katMock = new Mock<Crafts.Client.Api.IKatApi>();


        katMock.Setup(k => k.KatAllGetAsync(0, default)).ReturnsAsync(KatResponse());
        Console.WriteLine(JsonConvert.SerializeObject(KatResponse()));
        service = new ProfitChangeService(null, katMock.Object, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3 * 2 + 1, changes.Count);
        Assert.AreEqual(-20, changes[0].Amount);
        Assert.AreEqual(-100_000, changes[1].Amount, changes[1].Label);
        Assert.AreEqual("Kat materials for EPIC", changes[2].Label);
        Assert.AreEqual(-50, changes[2].Amount);
        Console.WriteLine(JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-41100170, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task AbilityScrolls()
    {
        // {"enchantments":[],"uuid":"8748045335304745869fa27afd7cf781","count":1,"startingBid":649999979,"tag":"NECRON_HANDLE","itemName":"Necron's Handle","start":"2023-01-20T06:14:58","end":"2023-01-20T06:29:09","auctioneerId":"0ca2593d6806476780068c4e45bd1006","profileId":"aee34ca707784cc0b1506466177ccb19","coopMembers":null,"highestBidAmount":649999979,"bids":[{"bidder":"7c5ff00eb1e04f50acd28554c911dbb4","profileId":"unknown","amount":649999979,"timestamp":"2023-01-20T06:28:37"}],"anvilUses":0,"nbtData":{"data":{"uid":"e5337b00fecb"}},"itemCreatedAt":"2023-01-20T00:43:00","reforge":"None","category":"MISC","tier":"EPIC","bin":true,"flatNbt":{"uid":"e5337b00fecb"}}
        var buy = new ColorSaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "NECRON_HANDLE",
            HighestBidAmount = 1000,
            FlatNbt = new(),
            Tier = Api.Client.Model.Tier.EPIC
        };
        // {"enchantments":[],"uuid":"247f6a20f72448369fcd4acd8876d3c7","count":1,"startingBid":1619999000,"tag":"HYPERION","itemName":"Heroic Hyperion ✪✪✪✪✪","start":"2023-01-20T06:30:51","end":"2023-01-20T21:07:09","auctioneerId":"7c5ff00eb1e04f50acd28554c911dbb4","profileId":"81d6550f356d4304815cbd795cd27b3f","coopMembers":null,"highestBidAmount":1619999000,"bids":[{"bidder":"4bbd3840b07d49a78b1c313fd8e63d1c","profileId":"unknown","amount":1619999000,"timestamp":"2023-01-20T21:05:31"}],"anvilUses":0,"nbtData":{"data":{"rarity_upgrades":1,"hpc":15,"upgrade_level":5,"uid":"e5337b00fecb","ability_scroll":["IMPLOSION_SCROLL","SHADOW_WARP_SCROLL","WITHER_SHIELD_SCROLL"]}},"itemCreatedAt":"2023-01-20T00:43:00","reforge":"Heroic","category":"WEAPON","tier":"MYTHIC","bin":true,"flatNbt":{"rarity_upgrades":"1","hpc":"15","upgrade_level":"5","uid":"e5337b00fecb","ability_scroll":"IMPLOSION_SCROLL SHADOW_WARP_SCROLL WITHER_SHIELD_SCROLL"}}
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 1000,
            FlatenedNBT = new()
            {
                { "ability_scroll", "IMPLOSION_SCROLL SHADOW_WARP_SCROLL WITHER_SHIELD_SCROLL" }
            },
            Tier = Core.Tier.LEGENDARY
        };
        var craftsApi = new Mock<Crafts.Client.Api.ICraftsApi>();
        craftsApi.Setup(c => c.CraftsAllGetAsync(0, default)).ReturnsAsync(() => new() {
            new() { ItemId = "HYPERION", Ingredients = new() { new() { ItemId = "NECRON_HANDLE", Count = 1 }
            } } });
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 200_000_000 });
        var itemsApi = new Mock<Items.Client.Api.IItemsApi>();
        itemsApi.Setup(i => i.ItemItemTagGetAsync("HYPERION", It.IsAny<bool?>(), It.IsAny<int>(), default)).ReturnsAsync(() => new() { Tag = "HYPERION", Tier = Items.Client.Model.Tier.LEGENDARY });
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(4, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-600000020, changes.Sum(c => c.Amount));
    }

    private List<KatUpgradeResult> KatResponse()
    {
        var all = new List<KatUpgradeResult>()
        {
            new ()
            {
                PurchaseCost = 5000,
                TargetRarity = Crafts.Client.Model.Tier.RARE,
                CoreData = new ()
                {
                    Name = "Enderman",
                    BaseRarity = Crafts.Client.Model.Tier.UNCOMMON,
                    Hours = 48,
                    Cost = 5000
                }
            },
            new ()
            {
                PurchaseCost = 100000,
                TargetRarity = Crafts.Client.Model.Tier.EPIC,
                CoreData = new ()
                {
                    Name = "Enderman",
                    BaseRarity = Crafts.Client.Model.Tier.RARE,
                    Hours = 144,
                    Cost = 100000
                }
            },
            new ()
            {
                PurchaseCost = 40000000,
                TargetRarity = Crafts.Client.Model.Tier.LEGENDARY,
                CoreData = new ()
                {
                    Name = "Enderman",
                    BaseRarity = Crafts.Client.Model.Tier.EPIC,
                    Hours = 288,
                    Cost = 40000000,
                    Material = "ENCHANTED_EYE_OF_ENDER",
                    Amount = 8
                }
            },
            new ()
            {
                PurchaseCost = 1000000,
                TargetRarity = Crafts.Client.Model.Tier.MYTHIC,
                CoreData = new ()
                {
                    Name = "Enderman",
                    BaseRarity = Crafts.Client.Model.Tier.LEGENDARY,
                    Hours = 1,
                    Cost = 1000000,
                    Material = "ENDERMAN_CORTEX_REWRITER",
                    Amount = 1
                }
            }
        };
        foreach (var item in all)
        {
            var tagProp = item.CoreData.GetType().GetProperty("ItemTag");
            tagProp.SetValue(item.CoreData, "PET_ENDERMAN");
            var materialCostProp = item.GetType().GetProperty("MaterialCost");
            materialCostProp.SetValue(item, 50);
            var upgradeCost = item.GetType().GetProperty("UpgradeCost");
            upgradeCost.SetValue(item, item.CoreData.Cost);
        }
        return all;
    }
}