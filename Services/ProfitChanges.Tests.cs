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
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object);
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
        service = new ProfitChangeService(null, katMock.Object, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3 * 2 + 1, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-20, changes[0].Amount);
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
        Assert.AreEqual(-41100170, changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(null, katMock.Object, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3, changes.Count);
        Assert.AreEqual(-40, changes[0].Amount);
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
        service = new ProfitChangeService(pricesApi.Object, katMock.Object, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3, changes.Count);
        Assert.AreEqual(-4000000, changes[0].Amount);
        Assert.AreEqual(-64000000, changes[1].Amount, changes[1].Label);
        Assert.AreEqual("Kat cost for EPIC", changes[2].Label);
        Assert.AreEqual(-192625000, changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(pricesApi.Object, katMock.Object, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Console.WriteLine(JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(3, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-4000000, changes[0].Amount);
        Assert.AreEqual(-64000000, changes[1].Amount, changes[1].Label);
        Assert.AreEqual("Kat cost for MYTHIC", changes[2].Label);
        Assert.AreEqual(-192625000, changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(pricesApi.Object, null, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual(-100000020, changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count);
        Assert.AreEqual(-(5_000_000 + 600_000 + 200_000), changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(1, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-200_000, changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(4, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-600000020, changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
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
            FlatenedNBT = new(){{"ethermerge", "1"}},
            Tier = Core.Tier.EPIC
        };
        var sell = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "ASPECT_OF_THE_VOID",
            HighestBidAmount = 10_000_000,
            FlatenedNBT = new(){{"ethermerge", "1"}},
            Tier = Core.Tier.EPIC
        };
        var json = JsonConvert.SerializeObject(buy);
        buy = JsonConvert.DeserializeObject<ApiSaveAuction>(json);
        var pricesApi = new Mock<Api.Client.Api.IPricesApi>();
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 200_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
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
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-80200000, changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-100200000, changes.Sum(c => c.Amount));
        Assert.AreEqual("100x Thunder in a bottle", changes[1].Label);
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
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 2_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-2_200_000, changes.Sum(c => c.Amount));
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_SHARPNESS_6", null, 0, default), Times.Once);
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
        pricesApi.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), null, 0, default)).ReturnsAsync(() => new() { Median = 20_000_000 });
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-20_200_000, changes.Sum(c => c.Amount));
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("ENCHANTMENT_ULTIMATE_CHIMERA_3", null, 0, default), Times.Once);
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

        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object);
        var changes = await service.GetChanges(buy, sell).ToListAsync();

        Assert.AreEqual(2, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-60_200_000, changes.Sum(c => c.Amount));
    }

    [Test]
    public async Task AddedMasterStars()
    {
        var buy = new Core.SaveAuction()
        {
            Uuid = Guid.NewGuid().ToString("N"),
            Tag = "HYPERION",
            HighestBidAmount = 879_000_000,
            FlatenedNBT = new(),
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
        service = new ProfitChangeService(pricesApi.Object, null, null, NullLogger<ProfitChangeService>.Instance, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(6, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual(-69580000, changes.Sum(c => c.Amount));
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("FOURTH_MASTER_STAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("THIRD_MASTER_STAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("SECOND_MASTER_STAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("FIRST_MASTER_STAR", null, 0, default), Times.Once);
        pricesApi.Verify(p => p.ApiItemPriceItemTagGetAsync("THE_ART_OF_WAR", null, 0, default), Times.Once);

        Assert.AreEqual("Used SECOND_MASTER_STAR to upgraded upgrade_level to 9", changes[3].Label);

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
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(4, changes.Count, JsonConvert.SerializeObject(changes, Formatting.Indented));
        Assert.AreEqual("crafting material GIANT_FRAGMENT_LASER x8", changes[1].Label);
        Assert.AreEqual(-8_000_000, changes[1].Amount);
        Assert.AreEqual("crafting material INCLUDE x10", changes[2].Label);
        Assert.AreEqual(-10_000_000, changes[2].Amount);
        Assert.AreEqual("crafting material WITHER_CATALYST x24", changes[3].Label);
        Assert.AreEqual(-24_000_000, changes[3].Amount);
        Assert.AreEqual(-42000020, changes.Sum(c => c.Amount));
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
        service = new ProfitChangeService(pricesApi.Object, null, craftsApi.Object, NullLogger<ProfitChangeService>.Instance, itemsApi.Object);
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