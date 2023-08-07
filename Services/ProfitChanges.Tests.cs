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
    public async Task EnderRelicToArtifactRecombed()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "ENDER_ARTIFACT",
            HighestBidAmount = 1000,
            FlatenedNBT = new() { { "rarity_upgrades", "1" } },
            Tier = Core.Tier.LEGENDARY
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "ENDER_RELIC",
            HighestBidAmount = 10000,
            FlatenedNBT = new() { { "rarity_upgrades", "1" } },
            Tier = Core.Tier.MYTHIC
        };
        var craftsApi = new Mock<Crafts.Client.Api.ICraftsApi>();
        craftsApi.Setup(c => c.CraftsAllGetAsync(0, default)).ReturnsAsync(() => new() {
            new() { ItemId = "ENDER_RELIC", Ingredients = new() {
                new() { ItemId = "ENDER_ARTIFACT", Count = 1 },
                new() { ItemId = "ENCHANTED_OBSIDIAN", Count = 128 },
                new() { ItemId = "ENCHANTED_EYE_OF_ENDER", Count = 96 },
                new() { ItemId = "EXCEEDINGLY_RARE_ENDER_ARTIFACT_UPGRADER", Count = 1 },
            } } });
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
                .ReturnsAsync(() => new() { Median = 200_000_000 });
        var itemsApi = new Mock<Items.Client.Api.IItemsApi>();
        itemsApi.Setup(i => i.ItemItemTagGetAsync("ENDER_RELIC", It.IsAny<bool?>(), It.IsAny<int>(), default))
                .ReturnsAsync(() => new() { Tag = "ENDER_RELIC", Tier = Items.Client.Model.Tier.LEGENDARY });
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object, null);
        var result = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(4, result.Count);
    }

    [Test]
    public async Task EndermanMultiLevel()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_ENDERMAN",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.RARE
        };
        var sell = new Core.SaveAuction()
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
        service = new ProfitChangeService(null, katMock.Object, null, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3 * 2 + 1, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-1210, changes[0].Amount);
        Assert.AreEqual(-100_000, changes[1].Amount, changes[1].Label);
        Assert.AreEqual("Kat materials for EPIC", changes[2].Label);
        Assert.AreEqual(-50, changes[2].Amount);
        var index = 1;
        foreach (var item in changes.Where((x, i) => i % 2 == 1).Take(2))
        {
            Assert.AreEqual("Kat cost for " + (Core.Tier.RARE + index++), item.Label);
        }
        Assert.AreEqual("Kat materials for " + Core.Tier.MYTHIC, changes.Last().Label);
        Console.WriteLine(JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-41101360, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task EnderDragonSingleLevel()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_ENDER_DRAGON",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_ENDER_DRAGON",
            HighestBidAmount = 2000,
            FlatenedNBT = new(),
            Tier = Core.Tier.LEGENDARY
        };
        var katMock = new Mock<Crafts.Client.Api.IKatApi>();
        katMock.Setup(k => k.KatAllGetAsync(0, default)).ReturnsAsync(KatResponse("PET_ENDER_DRAGON"));
        service = new ProfitChangeService(null, katMock.Object, null, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3, changes.Count);
        Assert.AreEqual(-1220, changes[0].Amount);
        Assert.AreEqual(-40000000, changes[1].Amount, changes[1].Label);
        Assert.AreEqual("Kat materials for LEGENDARY", changes[2].Label);
    }

    [Test]
    public async Task ScathaUpgrade()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_SCATHA",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.RARE
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_SCATHA",
            HighestBidAmount = 200_000_000,
            FlatenedNBT = new(),
            Tier = Core.Tier.EPIC
        };
        var katMock = new Mock<Crafts.Client.Api.IKatApi>();
        katMock.Setup(k => k.KatAllGetAsync(0, default)).ReturnsAsync(KatResponse("PET_ENDER_DRAGON"));
        katMock.Setup(k => k.KatRawGetAsync(0, default)).ReturnsAsync(new List<Crafts.Client.Model.KatUpgradeCost>()
        {
            new Crafts.Client.Model.KatUpgradeCost()
            {
                Name = "Scatha",
                BaseRarity = Crafts.Client.Model.Tier.RARE,
                Hours = 168,
                Cost = 125000000,
                Material = "ENCHANTED_HARD_STONE",
                Amount = 64
            }
        });
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
                .ReturnsAsync(() => new() { Median = 1_000_000 });
        service = new ProfitChangeService(pricesApi.Object, katMock.Object, null, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3, changes.Count);
        Assert.AreEqual(-7001200, changes[0].Amount);
        Assert.AreEqual(-64000000, changes[1].Amount, changes[1].Label);
        Assert.AreEqual("Kat cost for EPIC", changes[2].Label);
        Assert.AreEqual(-195626200, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task BatUpgrade()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_BAT",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.LEGENDARY
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_BAT",
            HighestBidAmount = 200_000_000,
            FlatenedNBT = new(),
            Tier = Core.Tier.MYTHIC
        };
        var katMock = new Mock<Crafts.Client.Api.IKatApi>();
        katMock.Setup(k => k.KatAllGetAsync(0, default)).ReturnsAsync(KatResponse("PET_ENDER_DRAGON"));
        katMock.Setup(k => k.KatRawGetAsync(0, default)).ReturnsAsync(new List<Crafts.Client.Model.KatUpgradeCost>()
        {
            new Crafts.Client.Model.KatUpgradeCost()
            {
                Name = "Bat",
                BaseRarity = Crafts.Client.Model.Tier.LEGENDARY,
                Hours = 168,
                Cost = 125000000,
                Material = "ENCHANTED_HARD_STONE",
                Amount = 64
            }
        });
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
                .ReturnsAsync(() => new() { Median = 1_000_000 });
        service = new ProfitChangeService(pricesApi.Object, katMock.Object, null, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Console.WriteLine(JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(3, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-7_001_200, changes[0].Amount);
        Assert.AreEqual(-64000000, changes[1].Amount, changes[1].Label);
        Assert.AreEqual("Kat cost for MYTHIC", changes[2].Label);
        Assert.AreEqual(-195626200, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task DropletWispUpgrade()
    {
        // {"enchantments":[],"uuid":"00fdf0dffe0f4f4497b60f12192df007","count":1,"startingBid":18799000,"tag":"PET_DROPLET_WISP","itemName":"[Lvl 25] Droplet Wisp","start":"2023-05-25T20:56:53","end":"2023-05-27T00:48:02","auctioneerId":"92a06abd576c4e56bde3b4298016ec57","profileId":null,"coop":null,"coopMembers":null,"highestBidAmount":18799000,"bids":[{"bidder":"e93dc3450d1f428a9acd60ee768ad750","profileId":"unknown","amount":18799000,"timestamp":"2023-05-27T00:48:02"}],"anvilUses":0,"nbtData":{"data":{"petInfo":"{\"type\":\"DROPLET_WISP\",\"active\":false,\"exp\":15616.0,\"tier\":\"UNCOMMON\",\"hideInfo\":false,\"candyUsed\":0,\"uuid\":\"94b9855c-72d6-4cc0-b046-345863b9ee51\",\"hideRightClick\":false,\"extraData\":{\"blaze_kills\":200500}}","uid":"345863b9ee51"}},"itemCreatedAt":"2023-05-25T16:55:00","reforge":"None","category":"MISC","tier":"UNCOMMON","bin":true,"flatNbt":{"type":"DROPLET_WISP","active":"False","exp":"15616","tier":"UNCOMMON","hideInfo":"False","candyUsed":"0","uuid":"94b9855c-72d6-4cc0-b046-345863b9ee51","hideRightClick":"False","uid":"345863b9ee51","blaze_kills":"200500"}}
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_DROPLET_WISP",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.RARE
        };
        // {"enchantments":[],"uuid":"0d328f25303f49e189805d9507a73ced","count":1,"startingBid":200000000,"tag":"PET_FROST_WISP","itemName":"[Lvl 100] Frost Wisp","start":"2023-07-28T10:53:18","end":"2023-07-28T10:53:54","auctioneerId":"e93dc3450d1f428a9acd60ee768ad750","profileId":null,"coop":null,"coopMembers":null,"highestBidAmount":200000000,"bids":[{"bidder":"75eaf1d0664f49e69d1b61857b864393","profileId":"5fa85f9232b3405f93780fa5e7a0bf31","amount":200000000,"timestamp":"2023-07-28T10:53:58"}],"anvilUses":0,"nbtData":{"data":{"petInfo":"{\"type\":\"FROST_WISP\",\"active\":false,\"exp\":1.2773376E7,\"tier\":\"RARE\",\"hideInfo\":false,\"heldItem\":\"CROCHET_TIGER_PLUSHIE\",\"candyUsed\":0,\"uuid\":\"94b9855c-72d6-4cc0-b046-345863b9ee51\",\"hideRightClick\":false,\"extraData\":{\"blaze_kills\":205712.0}}","uid":"345863b9ee51"}},"itemCreatedAt":"2023-07-27T22:45:00","reforge":"None","category":"MISC","tier":"RARE","bin":true,"flatNbt":{"type":"FROST_WISP","active":"False","exp":"12773376","tier":"RARE","hideInfo":"False","heldItem":"CROCHET_TIGER_PLUSHIE","candyUsed":"0","uuid":"94b9855c-72d6-4cc0-b046-345863b9ee51","hideRightClick":"False","uid":"345863b9ee51","blaze_kills":"205712"}}
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_FROST_WISP",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.EPIC
        };
        var katMock = new Mock<Crafts.Client.Api.IKatApi>();
        katMock.Setup(k => k.KatAllGetAsync(0, default)).ReturnsAsync(KatResponse("PET_ENDER_DRAGON"));
        katMock.Setup(k => k.KatRawGetAsync(0, default)).ReturnsAsync(new List<Crafts.Client.Model.KatUpgradeCost>()
        {
            new Crafts.Client.Model.KatUpgradeCost()
            {
                Name = "Droplet Wisp",
                BaseRarity = Crafts.Client.Model.Tier.RARE,
                Hours = 168,
                Cost = 125000000,
                Material = "ENCHANTED_HARD_STONE",
                Amount = 64
            }
        });
        var craftsApi = new Mock<Crafts.Client.Api.ICraftsApi>();
        craftsApi.Setup(c => c.CraftsAllGetAsync(0, default)).ReturnsAsync(() => new() {
            new() { ItemId = "UPGRADE_STONE_FROST", CraftCost = 500_000}});
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
                .ReturnsAsync(() => new() { Median = 1_000_000 });
        service = new ProfitChangeService(pricesApi.Object, katMock.Object, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual(-500_000, changes[1].Amount);
        Assert.AreEqual("Wisp upgrade stone for FROST", changes[1].Label);
    }

    [Test]
    public async Task TierBoostAddition()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_ENDERMAN",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.RARE
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_ENDERMAN",
            HighestBidAmount = 1000,
            FlatenedNBT = new() { { "heldItem", "PET_ITEM_TIER_BOOST" } },
            Tier = Core.Tier.MYTHIC
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
                .ReturnsAsync(() => new() { Median = 100_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual(-100001210, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task ReforgeChange()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "FERMENTO_BOOTS",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.RARE
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "FERMENTO_BOOTS",
            HighestBidAmount = 10_000_000,
            Reforge = Core.ItemReferences.Reforge.mossy,
            Tier = Core.Tier.MYTHIC
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
                .ReturnsAsync(() => new() { Median = 5_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual(-(5_000_000 + 600_000 + 201200), changes.Sum(c => c.Amount));
    }
    [Test]
    public async Task AoteReforgeNaming()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "AOTE",
            HighestBidAmount = 1000,
            Reforge = Core.ItemReferences.Reforge.aote_stone,
            FlatenedNBT = new(),
            Tier = Core.Tier.RARE
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "AOTE",
            HighestBidAmount = 10_000_000,
            Reforge = Core.ItemReferences.Reforge.warped_on_aote,
            Tier = Core.Tier.MYTHIC
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
                .ReturnsAsync(() => new() { Median = 5_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(1, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-201200, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task AbilityScrolls()
    {
        // {"enchantments":[],"uuid":"8748045335304745869fa27afd7cf781","count":1,"startingBid":649999979,"tag":"NECRON_HANDLE","itemName":"Necron's Handle","start":"2023-01-20T06:14:58","end":"2023-01-20T06:29:09","auctioneerId":"0ca2593d6806476780068c4e45bd1006","profileId":"aee34ca707784cc0b1506466177ccb19","coopMembers":null,"highestBidAmount":649999979,"bids":[{"bidder":"7c5ff00eb1e04f50acd28554c911dbb4","profileId":"unknown","amount":649999979,"timestamp":"2023-01-20T06:28:37"}],"anvilUses":0,"nbtData":{"data":{"uid":"e5337b00fecb"}},"itemCreatedAt":"2023-01-20T00:43:00","reforge":"None","category":"MISC","tier":"EPIC","bin":true,"flatNbt":{"uid":"e5337b00fecb"}}
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "NECRON_HANDLE",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.EPIC
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
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(4, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-600001210, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task RarityUpgrade()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new()
            {
                { "rarity_upgrades", "1" }
            },
            Tier = Core.Tier.LEGENDARY
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 200_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
    }

    [Test]
    public async Task NotAddedEthermerge()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "ASPECT_OF_THE_VOID",
            HighestBidAmount = 1000,
            FlatenedNBT = new() { { "ethermerge", "1" } },
            Tier = Core.Tier.EPIC
        };
        var sell = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "ASPECT_OF_THE_VOID",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new() { { "ethermerge", "1" } },
            Tier = Core.Tier.EPIC
        };
        var json = JsonConvert.SerializeObject(buy);
        buy = JsonConvert.DeserializeObject<ApiSaveAuction>(json);
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 200_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Console.WriteLine(json);
        Console.WriteLine(JsonConvert.SerializeObject(buy, Formatting.Indented));
        Assert.AreEqual(1, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
    }

    [Test]
    public async Task PulseRingUpgrade()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PULSE_RING",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PULSE_RING",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new(),
            Tier = Core.Tier.LEGENDARY
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 1_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-80201200, changes.Sum(c => c.Amount));
        Assert.AreEqual("80x Thunder in a bottle", changes[1].Label);
    }
    [Test]
    public async Task PulseRingUpgradeFull()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PULSE_RING",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.UNCOMMON
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PULSE_RING",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new(),
            Tier = Core.Tier.MYTHIC
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 1_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-100201200, changes.Sum(c => c.Amount));
        Assert.AreEqual("100x Thunder in a bottle", changes[1].Label);
    }

    [Test]
    public async Task CombinedAttributes()
    {
        var buy = CreateAuction("ATTRIBUTE_SHARD");
        buy.FlatenedNBT["mana_pool"] = "2";
        var sell = CreateAuction("ATTRIBUTE_SHARD");
        sell.FlatenedNBT["mana_pool"] = "3";
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("ATTRIBUTE_SHARD", new() { { "mana_pool", "2" } }, 0, default)).ReturnsAsync(() => new() { Median = 5000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var result = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(-5000, result[1].Amount);
        sell.FlatenedNBT["mana_pool"] = "5";
        result = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(-35000, result[1].Amount);
    }
    [Test]
    public async Task CombineHighLevelAttribut()
    {
        var buy = CreateAuction("AURORA_CHESTPLATE");
        buy.FlatenedNBT["mana_pool"] = "4";
        var sell = CreateAuction("AURORA_CHESTPLATE");
        sell.FlatenedNBT["mana_pool"] = "10";
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("ATTRIBUTE_SHARD", new() { { "mana_pool", "2" } }, 0, default)).ReturnsAsync(() => new() { Median = 2_000_000 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("AURORA_CHESTPLATE", new() { { "mana_pool", "2" } }, 0, default)).ReturnsAsync(() => new() { Median = 5_000_000 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("AURORA_CHESTPLATE", new() { { "mana_pool", "5" } }, 0, default)).ReturnsAsync(() => new() { Median = 10_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var result = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(-312000000, result[1].Amount);
    }
    [Test]
    public async Task WheelOfFateChangesAttributeType()
    {
        var buy = CreateAuction("MOLTEN_CLOAK");
        buy.FlatenedNBT["blazing_resistance"] = "5";
        buy.FlatenedNBT["breeze"] = "4";
        var sell = new Core.SaveAuction(buy);
        sell.FlatenedNBT = new(){
            {"mana_regeneration","4"},
            {"dominance","5"}
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("WHEEL_OF_FATE", null, 0, default)).ReturnsAsync(() => new() { Median = 12_000_000 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("MOLTEN_CLOAK", new() { { "mana_regeneration", "2" } }, 0, default)).ReturnsAsync(() => new() { Median = 5000 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("MOLTEN_CLOAK", new() { { "dominance", "2" } }, 0, default)).ReturnsAsync(() => new() { Median = 5000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var result = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(-12000000, result[1].Amount);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), 0, default), Times.Once);
    }

    [Test]
    public async Task UseShardCostIfCheaper()
    {
        var buy = CreateAuction("GAUNTLET_OF_CONTAGION");
        buy.FlatenedNBT["mana_regeneration"] = "2";
        var sell = CreateAuction("GAUNTLET_OF_CONTAGION");
        sell.FlatenedNBT["mana_regeneration"] = "3";
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("GAUNTLET_OF_CONTAGION", new() { { "mana_regeneration", "2" } }, 0, default)).ReturnsAsync(() => new() { Median = 13_000_000 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("ATTRIBUTE_SHARD", new() { { "mana_regeneration", "2" } }, 0, default)).ReturnsAsync(() => new() { Median = 2_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var result = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(-2000000, result[1].Amount);
    }

    [Test]
    public async Task Enchantments()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Enchantments = new() { new() { Type = Core.Enchantment.EnchantmentType.ultimate_wisdom, Level = 5 } },
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new(),
            Enchantments = new() { new() { Type = Core.Enchantment.EnchantmentType.sharpness, Level = 6 },
                new(){Type = Core.Enchantment.EnchantmentType.ultimate_wisdom, Level = 5} },
            Tier = Core.Tier.LEGENDARY
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(() => new() { Median = 2_000_000, Max = 3_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-2_201_200, changes.Sum(c => c.Amount));
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_SHARPNESS_6", null, 0, default), Times.Once);
    }

    [Test]
    public async Task NoVolumeEnchantFallbackToLvl1()
    {
        var buy = CreateAuction("HYPERION");
        buy.Enchantments = new() { new() { Type = Core.Enchantment.EnchantmentType.ultimate_chimera, Level = 2 } };
        var sell = CreateAuction("HYPERION");
        sell.Enchantments = new() { new() { Type = Core.Enchantment.EnchantmentType.ultimate_chimera, Level = 4 } };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_ULTIMATE_CHIMERA_1", null, 0, default)).ReturnsAsync(() => new() { Median = 100_000_000, Max = 105900000 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_ULTIMATE_CHIMERA_4", null, 0, default)).ReturnsAsync(() => new() { Median = 231527, Max = 0 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_ULTIMATE_CHIMERA_2", null, 0, default)).ReturnsAsync(() => new() { Median = 3231527, Max = 0 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var result = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(-600_000_000, result[1].Amount);
    }

    [Test]
    public async Task EnchantmentUpgrade()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Enchantments = new() { new() { Type = Core.Enchantment.EnchantmentType.ultimate_chimera, Level = 3 } },
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new(),
            Enchantments = new() {
                new(){Type = Core.Enchantment.EnchantmentType.ultimate_chimera, Level = 4} },
            Tier = Core.Tier.LEGENDARY
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(() => new() { Median = 20_000_000, Max = 30_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-20_201_200, changes.Sum(c => c.Amount));
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_ULTIMATE_CHIMERA_3", null, 0, default), Times.Once);
    }

    [Test]
    public async Task ImpossibleEnchantUpgrade()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Enchantments = new() { new() { Type = Core.Enchantment.EnchantmentType.sharpness, Level = 6 } },
            Tier = Core.Tier.LEGENDARY
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new(),
            Enchantments = new() {
                new(){Type = Core.Enchantment.EnchantmentType.sharpness, Level = 7} },
            Tier = Core.Tier.LEGENDARY
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_SHARPNESS_7", null, 0, default)).ReturnsAsync(() => new() { Median = 20_000_000, Max = 20_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-20_201_200, changes.Sum(c => c.Amount));
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_SHARPNESS_7", null, 0, default), Times.Once);
    }
    [Test]
    public async Task FieryKuudraCore()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "BURNING_KUUDRA_CORE",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Enchantments = new(),
            Tier = Core.Tier.RARE
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "FIERY_KUUDRA_CORE",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new(),
            Enchantments = new(),
            Tier = Core.Tier.EPIC
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 20_000_000 });
        var craftsApi = new Mock<Crafts.Client.Api.ICraftsApi>();
        craftsApi.Setup(c => c.CraftsAllGetAsync(0, default)).ReturnsAsync(() => new() {
            new() { ItemId = "FIERY_KUUDRA_CORE", Ingredients = new() {
                new() { ItemId = "BURNING_KUUDRA_CORE", Count = 4 }
                }}});
        var itemsApi = new Mock<Items.Client.Api.IItemsApi>();
        itemsApi.Setup(i => i.ItemItemTagGetAsync("FIERY_KUUDRA_CORE", It.IsAny<bool?>(), It.IsAny<int>(), default))
            .ReturnsAsync(() => new() { Tag = "FIERY_KUUDRA_CORE", Tier = Items.Client.Model.Tier.EPIC });

        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();

        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-60201200, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task AddedMasterStars()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 879_000_000,
            FlatenedNBT = new(){
                {"upgrade_level", "1"}},
            Enchantments = new(),
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 979_000_000,
            FlatenedNBT = new() {
                {"upgrade_level", "9"},
                {"art_of_war_count", "1"}
            },
            Enchantments = new(),
            Tier = Core.Tier.LEGENDARY
        };
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 10_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null,
            NullLogger<ProfitChangeService>.Instance, null,
            new HypixelItemService(new System.Net.Http.HttpClient(), NullLogger<HypixelItemService>.Instance));
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(10, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-32084266200, changes.Sum(c => c.Amount));
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("FOURTH_MASTER_STAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("THIRD_MASTER_STAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("SECOND_MASTER_STAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("FIRST_MASTER_STAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("THE_ART_OF_WAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("ESSENCE_WITHER", null, 0, default), Times.Exactly(4));

        Assert.AreEqual("WITHER essence x500 to add star", changes[2].Label);
        Assert.AreEqual("Used SECOND_MASTER_STAR to upgraded upgrade_level to 9", changes[7].Label);

    }

    [Test]
    public async Task AddedExp()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_BAT",
            HighestBidAmount = 5_000_000,
            FlatenedNBT = new(){
                {"exp", "500000"}},
            Enchantments = new(),
            ItemName = "[Lvl 30] Bat",
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "PET_BAT",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new() {
                {"exp", "1000000.1"}
            },
            Enchantments = new(),
            ItemName = "[Lvl 60] Bat",
            Tier = Core.Tier.EPIC
        };
        SetupPetLevelService();
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-398413, changes.Sum(c => c.Amount));
    }

    private void SetupPetLevelService()
    {
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("PET_BAT", new() { { "PetLevel", "1" }, { "Rarity", "LEGENDARY" } }, 0, default)).ReturnsAsync(() => new() { Median = 10_000_000 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("PET_BAT", new() { { "PetLevel", "100" }, { "Rarity", "LEGENDARY" } }, 0, default)).ReturnsAsync(() => new() { Median = 20_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null,
            NullLogger<ProfitChangeService>.Instance, null,
            new HypixelItemService(new System.Net.Http.HttpClient(), NullLogger<HypixelItemService>.Instance));
    }

    private static Core.SaveAuction CreateAuction(string tag, string itemName = "test", int highestBidAmount = 1000, Core.Tier tier = Core.Tier.EPIC)
    {
        return new()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = tag,
            HighestBidAmount = highestBidAmount,
            Enchantments = new(),
            FlatenedNBT = new(),
            ItemName = itemName,
            Tier = tier
        };
    }

    [Test]
    public async Task AddedExpOverLvl100()
    {
        var buy = CreateAuction("PET_BAT", "[Lvl 30] Bat", 5_000_000, Core.Tier.LEGENDARY);
        buy.FlatenedNBT["exp"] = "125996318";
        var sell = CreateAuction("PET_BAT", "[Lvl 60] Bat", 10_000_000, Core.Tier.LEGENDARY);
        sell.FlatenedNBT["exp"] = "128176815.6";
        SetupPetLevelService();
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(1, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
    }
    [Test]
    public async Task AddedExpOverLvl100GoldenDragon()
    {
        var buy = CreateAuction("PET_GOLDEN_DRAGON", "[Lvl 102] Golden Dragon", 5_000_000, Core.Tier.LEGENDARY);
        buy.FlatenedNBT["exp"] = "25360717.32";
        var sell = CreateAuction("PET_GOLDEN_DRAGON", "[Lvl 103] Golden Dragon", 10_000_000, Core.Tier.LEGENDARY);
        sell.FlatenedNBT["exp"] = "29095472.42";
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("PET_GOLDEN_DRAGON", new() { { "PetLevel", "1" }, { "Rarity", "LEGENDARY" } }, 0, default)).ReturnsAsync(() => new() { Median = 600_000_000 });
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync("PET_GOLDEN_DRAGON", new() { { "PetLevel", "200" }, { "Rarity", "LEGENDARY" } }, 0, default)).ReturnsAsync(() => new() { Median = 1200_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null,
            NullLogger<ProfitChangeService>.Instance, null,
            new HypixelItemService(new System.Net.Http.HttpClient(), NullLogger<HypixelItemService>.Instance));
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("PET_GOLDEN_DRAGON", new() { { "PetLevel", "200" }, { "Rarity", "LEGENDARY" } }, 0, default), Times.Once);
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-10657764, changes[1].Amount);
    }

    [Test]
    public async Task MultiLevelCraft()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "NECRON_HANDLE",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.LEGENDARY
        };
        var craftsApi = new Mock<Crafts.Client.Api.ICraftsApi>();
        craftsApi.Setup(c => c.CraftsAllGetAsync(0, default)).ReturnsAsync(() => new() {
            new() { ItemId = "HYPERION", Ingredients = new() {
                new() { ItemId = "MadeUpForDepth", Count = 1 },
                new() { ItemId = "GIANT_FRAGMENT_LASER", Count = 8}}},
            new() { ItemId = "MadeUpForDepth", Ingredients = new() {
                new() { ItemId = "NECRON_BLADE", Count = 1 },
                new() { ItemId = "INCLUDE", Count = 10 }}},
            new() { ItemId = "GIANT_FRAGMENT_LASER", Ingredients = new() {
                new() { ItemId = "SHOULDNOTINCLUDE", Count = 10 }}},
            new() { ItemId = "NECRON_BLADE", Ingredients = new() {
                new() { ItemId = "NECRON_HANDLE", Count = 1 },
                new() { ItemId = "WITHER_CATALYST", Count = 24}} }
             });
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 1_000_000 });
        var itemsApi = new Mock<Items.Client.Api.IItemsApi>();
        itemsApi.Setup(i => i.ItemItemTagGetAsync("HYPERION", It.IsAny<bool?>(), It.IsAny<int>(), default)).ReturnsAsync(() => new() { Tag = "HYPERION", Tier = Items.Client.Model.Tier.LEGENDARY });
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(4, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual("crafting material GIANT_FRAGMENT_LASER x8", changes[1].Label);
        Assert.AreEqual(-8_000_000, changes[1].Amount);
        Assert.AreEqual("crafting material INCLUDE x10", changes[2].Label);
        Assert.AreEqual(-10_000_000, changes[2].Amount);
        Assert.AreEqual("crafting material WITHER_CATALYST x24", changes[3].Label);
        Assert.AreEqual(-24_000_000, changes[3].Amount);
        Assert.AreEqual(-42001210, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task MultiLevelCraftOriginal()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "NECRON_HANDLE",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.EPIC
        };
        var sell = new Coflnet.Sky.Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 1000,
            FlatenedNBT = new(),
            Tier = Core.Tier.LEGENDARY
        };
        var craftsApi = new Mock<Crafts.Client.Api.ICraftsApi>();
        craftsApi.Setup(c => c.CraftsAllGetAsync(0, default)).ReturnsAsync(() => new() {
            new() { ItemId = "HYPERION", Ingredients = new() {
                new() { ItemId = "NECRON_BLADE", Count = 1 },
                new() { ItemId = "WITHER_CATALYST", Count = 10 }}},
            new() { ItemId = "NECRON_BLADE", Ingredients = new() {
                new() { ItemId = "NECRON_HANDLE", Count = 1 },
                new() { ItemId = "WITHER_CATALYST", Count = 24}} }
             });
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 1_000_000 });
        var itemsApi = new Mock<Items.Client.Api.IItemsApi>();
        itemsApi.Setup(i => i.ItemItemTagGetAsync("HYPERION", It.IsAny<bool?>(), It.IsAny<int>(), default)).ReturnsAsync(() => new() { Tag = "HYPERION", Tier = Items.Client.Model.Tier.LEGENDARY });
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual("crafting material WITHER_CATALYST x10", changes[1].Label);

    }
    private List<KatUpgradeResult> KatResponse(string petTag = "PET_ENDERMAN")
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
            tagProp.SetValue(item.CoreData, petTag);
            var materialCostProp = item.GetType().GetProperty("MaterialCost");
            materialCostProp.SetValue(item, 50);
            var upgradeCost = item.GetType().GetProperty("UpgradeCost");
            upgradeCost.SetValue(item, item.CoreData.Cost);
        }
        return all;
    }
}
