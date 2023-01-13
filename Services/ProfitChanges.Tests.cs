using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.Api.Client.Model;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Crafts.Client.Model;
using NUnit.Framework;
using Moq;

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
        Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(KatResponse()));
        service = new ProfitChangeService(null, katMock.Object, null, null, null);
        var changes = await service.GetChanges(buy, sell).ToListAsync();
        Assert.AreEqual(3 * 2 + 1, changes.Count);
        Assert.AreEqual(-20, changes[0].Amount);
        Assert.AreEqual(-100_000, changes[1].Amount, changes[1].Label);
        Assert.AreEqual("Kat materials for EPIC", changes[2].Label);
        Assert.AreEqual(-50, changes[2].Amount);
        Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(changes, Newtonsoft.Json.Formatting.Indented));
        Assert.AreEqual(-41100170, changes.Sum(c => c.Amount));
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