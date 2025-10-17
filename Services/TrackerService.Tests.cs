using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.Core;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Diagnostics;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class TrackerServiceTests
{
    [Test]
    [TestCase("§7[Lvl 1] §6Bat", "[Lvl 60] Bat", "[Lvl 1->60] Bat")]
    [TestCase("[Lvl 30] Bat", "§7[Lvl 100] §6Bat", "§7[Lvl 30->100] §6Bat")]
    public void CheckLevelDisplay(string startName, string sellName, string expected)
    {
        var buy = new ApiSaveAuction()
        {
            ItemName = startName,
            FlatenedNBT = new() { { "exp", "500000" } },
            Tag = "PET_BAT"
        };
        var sell = new ApiSaveAuction()
        {
            ItemName = sellName,
            FlatenedNBT = new() { { "exp", "1000000.1" } },
            Tag = "PET_BAT"
        };
        var name = TrackerService.GetDisplayName(buy, sell);
        Assert.That(name, Is.EqualTo(expected));
    }

    [Test]
    public void ParseTradeItem()
    {
        var json = """
        {"Id":null,"ItemName":"§dJaded Helmet of Divan §4✦","Tag":"DIVAN_HELMET","ExtraAttributes":{"rarity_upgrades":1,
        "gems":{
            "JADE_1":{"uuid":"fc33c0c2-4611-46ef-b026-40d70a362998","quality":"FLAWLESS"},
            "JADE_0":{"uuid":"32af71e6-bebb-4dfe-90a8-5d5d40bb3399","quality":"FLAWLESS"},
            "unlocked_slots":["AMBER_0","AMBER_1","JADE_0","JADE_1","TOPAZ_0"],
            "AMBER_0":{"uuid":"07a39430-816e-4f46-a571-4df9dfa6ed82","quality":"FLAWLESS"},
            "AMBER_1":{"uuid":"659f15fc-5b01-40f8-b903-7c9ca9fddd3f","quality":"FLAWLESS"},
            "TOPAZ_0":{"uuid":"74a569a4-fc86-4bfb-9f78-9c5e4c42bd33","quality":"FLAWLESS"}},
        "modifier":"jaded","skin":"GEMSTONE_DIVAN","favorite_gemstone":4,"uid":"a68fc0f858a8","uuid":"9a03e307-926b-44ee-8df4-a68fc0f858a8","timestamp":1706457319539,"tier":8},
        "Enchantments":null,"Color":null,
        "Description":"§7§8Gemstone Divan Helmet Skin\n\n§7Health: §a+100\n§7Defense: §a+130\n§7Mining Speed: §a+290 §9(+60) §d(+150)\n§7Mining Fortune: §a+130 §9(+30) §d(+70)\n§7Pristine: §a+1.6 §d(+1.6)\n §5[§6⸕§5] §5[§a☘§5] §5[§6⸕§5] §5[§a☘§5] §5[§e✧§5]\n\n§6Ability: Color Swapper  §e§lLEFT CLICK\n§7Swaps this helmet's skin through\n§7your favorite Gemstone colors!\n\n§7Selected: §6Amber Gemstone\n\n§7§4❣ §cRequires §5Heart of the Mountain Tier 6§c.\n§d§l§ka§r §d§l§d§lMYTHIC HELMET §d§l§ka","Count":1}
        """;
        var trade = JsonConvert.DeserializeObject<PlayerState.Client.Model.Item>(json);
        var service = new RepresentationConverter(NullLogger<RepresentationConverter>.Instance, null);
        var auction = service.FromItemRepresent(trade);
        Assert.That(auction.ItemName, Is.EqualTo("§dJaded Helmet of Divan §4✦"));
        auction.FlatenedNBT.Should().Contain(new KeyValuePair<string, string>("AMBER_0", "FLAWLESS"));
    }

    [Test]
    public async Task UpdateBuyWithTradeState()
    {
        var buy = JsonConvert.DeserializeObject<ApiSaveAuction>("""
        {"enchantments":[],"uuid":"e308e8ec5b2e422ca8f0d829e7dbfdea","count":1,"startingBid":7150000,"tag":"ROD_OF_THE_SEA","itemName":"Rod of the Sea","start":"2025-07-30T20:10:22","end":"2025-07-31T16:56:16","auctioneerId":"6cbe27748b7745d2ac5be0e1e2d9f775","profileId":null,"coop":null,"coopMembers":null,"highestBidAmount":7150000,"bids":[{"bidder":"ae39c3216c764de9983e0bedaeb32781","profileId":"unknown","amount":7150000,"timestamp":"2025-07-31T16:56:16"}],"anvilUses":0,"nbtData":{"data":{"uid":"0e9fe2583dd2","uuid":"837cad89-874e-4ede-9a16-0e9fe2583dd2"}},"itemCreatedAt":"2025-07-30T20:03:36","reforge":"None","category":"MISC","tier":"LEGENDARY","bin":true,"flatNbt":{"uid":"0e9fe2583dd2","uuid":"837cad89-874e-4ede-9a16-0e9fe2583dd2"}}
        """);
        var tradeState = JsonConvert.DeserializeObject<PlayerState.Client.Model.Item>("""
        {"id":1130753106226150,"itemName":"§dPitchin' Rod of the Sea","tag":"ROD_OF_THE_SEA","extraAttributes":{"rarity_upgrades":1,"hook.uuid":"b27abe52-53d8-455b-ba93-7345c4cf13c1","modifier":"pitchin","line.uuid":"951e2044-187b-4df0-b61e-4ef1e6c31280","uid":"0e9fe2583dd2","uuid":"837cad89-874e-4ede-9a16-0e9fe2583dd2","timestamp":1753905816274,"tier":1,"sinker.uuid":"2de65430-0769-4ac6-a26a-7e3a645e0ff6","sinker.part":"junk_sinker","line.part":"speedy_line","hook.part":"common_hook"},"enchantments":{"angler":5,"blessing":5,"caster":5,"frail":5,"impaling":3,"looting":3,"luck_of_the_sea":5,"lure":5,"magnet":5,"piscary":5,"spiked_hook":5},"color":null,"description":null,"count":1}
        """);
        var service = new RepresentationConverter(NullLogger<RepresentationConverter>.Instance, null);
        service.TryUpdatingBuyState(buy, tradeState);
        buy.ItemName.Should().Be("§dPitchin' Rod of the Sea");
        buy.FlatenedNBT.Should().Contain(new KeyValuePair<string, string>("sinker.part", "junk_sinker"));
        buy.FlatenedNBT.Should().Contain(new KeyValuePair<string, string>("line.part", "speedy_line"));
        buy.FlatenedNBT.Should().Contain(new KeyValuePair<string, string>("hook.part", "common_hook"));
    }

    [Test]
    public async Task MultiItemTrade()
    {
        var trade = JsonConvert.DeserializeObject<Models.TradeModel>(FullTrade);
        var sniperclient = new Mock<ISniperClient>();
        sniperclient.Setup(s => s.GetPrices(It.IsAny<IEnumerable<Core.SaveAuction>>())).ReturnsAsync(new List<Sniper.Client.Model.PriceEstimate>
        {
            new (){Median = 200000000},
            new (){Median = 100000000},
            new (){Median = 100000000},
            new (){Median = 200000000},
            new (){Median = 50000000}
        });
        var service = new RepresentationConverter(NullLogger<RepresentationConverter>.Instance, sniperclient.Object);
        var auction = await service.ConvertToDummyAuctions(trade);
        auction.Should().HaveCount(5);
        auction[0].ItemName.Should().Be("§dJaded Boots of Divan");
        auction[0].HighestBidAmount.Should().Be(400000000); // each item traded for double of the value
    }

    private static string FullTrade = """
    {
    "UserId": "627115",
    "MinecraftUuid": "5e76d11b-f0d9-463a-b233-0bba2e8213d3",
    "Spent": [
        {
            "Id": null,
            "ItemName": "§dJaded Boots of Divan",
            "Tag": "DIVAN_BOOTS",
            "ExtraAttributes": {
                "rarity_upgrades": 1,
                "gems": {
                    "JADE_1": "PERFECT",
                    "JADE_0": "PERFECT",
                    "unlocked_slots": [
                        "AMBER_0",
                        "AMBER_1",
                        "JADE_0",
                        "JADE_1",
                        "TOPAZ_0"
                    ],
                    "AMBER_0": "PERFECT",
                    "AMBER_1": "PERFECT",
                    "TOPAZ_0": "PERFECT"
                },
                "runes": {
                    "COUTURE": 3
                },
                "modifier": "jaded",
                "uid": "59f4ffdca1b9",
                "uuid": "889ace16-2794-4971-8e6d-59f4ffdca1b9",
                "timestamp": 1720685512944,
                "tier": 8
            },
            "Enchantments": {
                "depth_strider": 3,
                "rejuvenate": 5,
                "growth": 5,
                "protection": 5
            },
            "Color": null,
            "Description": "§7Health: §a+155\n§7Defense: §a+130\n§7Mining Speed: §a+340 §9(+60) §d(+200)\n§7Mining Fortune: §a+160 §9(+30) §d(+100)\n§7Pristine: §a+2 §d(+2)\n§7Health Regen: §a+10\n §6[§6⸕§6] §6[§a☘§6] §6[§6⸕§6] §6[§a☘§6] §6[§e✧§6]\n\n§9Depth Strider III\n§7Reduces how much you are slowed in\n§7the water by §a100%§7.\n§9Growth V\n§7Grants §a+75 §c❤ Health§7.\n§9Protection V\n§7Grants §a+20 §a❈ Defense§7.\n§9Rejuvenate V\n§7Grants §c+10❣ Health Regen§7.\n\n§6◆ Couture Rune III\n\n§d§l§ka§r §d§l§d§lMYTHIC BOOTS §d§l§ka",
            "Count": 1
        },
        {
            "Id": null,
            "ItemName": "§dAuspicious Titanium Drill DR-X655",
            "Tag": "TITANIUM_DRILL_4",
            "ExtraAttributes": {
                "rarity_upgrades": 1,
                "polarvoid": 5,
                "drill_fuel": 29134,
                "modifier": "auspicious",
                "compact_blocks": 146684,
                "uuid": "ed271eec-f932-4277-9a01-65600a767f2c",
                "drill_part_upgrade_module": "goblin_omelette_sunny_side",
                "drill_part_engine": "amber_polished_drill_engine",
                "gems": {
                    "JADE_0": "PERFECT",
                    "MINING_0": "PERFECT",
                    "unlocked_slots": [
                        "JADE_0",
                        "MINING_0"
                    ],
                    "AMBER_0": "PERFECT",
                    "MINING_0_gem": "TOPAZ"
                },
                "uid": "65600a767f2c",
                "drill_part_fuel_tank": "perfectly_cut_fuel_tank",
                "timestamp": 1723785055498,
                "tier": 8
            },
            "Enchantments": {
                "efficiency": 5,
                "smelting_touch": 1,
                "fortune": 4,
                "compact": 7,
                "pristine": 5,
                "ultimate_wise": 5,
                "experience": 4
            },
            "Color": null,
            "Description": "§8Breaking Power 10\n\n§7Damage: §c+75\n§7Mining Speed: §a+2,210 §9[+50] §9(+60) §d(+100)\n§7Mining Fortune: §a+373 §9(+8) §d(+50)\n§7Pristine: §a+7 §d(+2)\n§7Mining Wisdom: §a+7\n §6[§6⸕§6] §6[§a☘§6] §6[§e✦§6]\n\n§9§d§lUltimate Wise V\n§9Compact VII §8146,684\n§9Efficiency V\n§9Experience IV\n§9Fortune IV\n§9Pristine V\n§9Smelting Touch I\n\n§aPerfectly-Cut Fuel Tank\n§7Increases the fuel capacity to §2100,000§7.\n\n§aAmber-Polished Drill Engine\n§7Grants §a§6+400⸕ Mining Speed§7.\n§7Grants §a§6+100☘ Mining Fortune§7.\n\n§aSunny Side Goblin Egg Part\n§7Grants §a+50 §6☘ Mining Fortune§7, but fuel\n§7consumption is doubled.\n\n§7Fuel: §229,134§8/100k\n\n§6Ability: Mining Speed Boost  §e§lRIGHT CLICK\n§7Grants §a+§a300% §6⸕ Mining Speed §7for\n§7§a20s§7.\n§8Cooldown: §a120s\n\n§9Auspicious Bonus\n§7Grants §a+8 §6☘ Mining Fortune§7, which\n§7increases your chance for multiple\n§7drops.\n\n§d§l§ka§r §d§l§d§lMYTHIC DRILL §d§l§ka",
            "Count": 1
        },
        {
            "Id": null,
            "ItemName": "§dJaded Chestplate of Divan",
            "Tag": "DIVAN_CHESTPLATE",
            "ExtraAttributes": {
                "rarity_upgrades": 1,
                "gems": {
                    "JADE_1": "PERFECT",
                    "JADE_0": "PERFECT",
                    "unlocked_slots": [
                        "AMBER_0",
                        "AMBER_1",
                        "JADE_0",
                        "JADE_1",
                        "TOPAZ_0"
                    ],
                    "AMBER_0": "PERFECT",
                    "AMBER_1": "PERFECT",
                    "TOPAZ_0": "PERFECT"
                },
                "modifier": "jaded",
                "uid": "f5dca5b4b191",
                "uuid": "a6f85bef-2cfb-4f4b-bda2-f5dca5b4b191",
                "timestamp": 1715515396718,
                "tier": 8
            },
            "Enchantments": {
                "rejuvenate": 5,
                "growth": 5,
                "protection": 5
            },
            "Color": null,
            "Description": "§7Health: §a+275\n§7Defense: §a+150\n§7Mining Speed: §a+340 §9(+60) §d(+200)\n§7Mining Fortune: §a+160 §9(+30) §d(+100)\n§7Pristine: §a+2 §d(+2)\n§7Health Regen: §a+10\n §6[§6⸕§6] §6[§a☘§6] §6[§6⸕§6] §6[§a☘§6] §6[§e✧§6]\n\n§9Growth V\n§7Grants §a+75 §c❤ Health§7.\n§9Protection V\n§7Grants §a+20 §a❈ Defense§7.\n§9Rejuvenate V\n§7Grants §c+10❣ Health Regen§7.\n\n§d§l§ka§r §d§l§d§lMYTHIC CHESTPLATE §d§l§ka",
            "Count": 1
        },
        {
            "Id": null,
            "ItemName": "§dJaded Leggings of Divan",
            "Tag": "DIVAN_LEGGINGS",
            "ExtraAttributes": {
                "rarity_upgrades": 1,
                "gems": {
                    "JADE_1": "PERFECT",
                    "JADE_0": "PERFECT",
                    "unlocked_slots": [
                        "AMBER_0",
                        "AMBER_1",
                        "JADE_0",
                        "JADE_1",
                        "TOPAZ_0"
                    ],
                    "AMBER_0": "PERFECT",
                    "AMBER_1": "PERFECT",
                    "TOPAZ_0": "PERFECT"
                },
                "modifier": "jaded",
                "uid": "0929f98c58b9",
                "uuid": "109f4072-f178-4851-8cf6-0929f98c58b9",
                "timestamp": 1715341196073,
                "tier": 8
            },
            "Enchantments": {
                "rejuvenate": 5,
                "protection": 5,
                "growth": 5
            },
            "Color": null,
            "Description": "§7Health: §a+205\n§7Defense: §a+190\n§7Mining Speed: §a+340 §9(+60) §d(+200)\n§7Mining Fortune: §a+160 §9(+30) §d(+100)\n§7Pristine: §a+2 §d(+2)\n§7Health Regen: §a+10\n §6[§6⸕§6] §6[§a☘§6] §6[§6⸕§6] §6[§a☘§6] §6[§e✧§6]\n\n§9Growth V\n§7Grants §a+75 §c❤ Health§7.\n§9Protection V\n§7Grants §a+20 §a❈ Defense§7.\n§9Rejuvenate V\n§7Grants §c+10❣ Health Regen§7.\n\n§d§l§ka§r §d§l§d§lMYTHIC LEGGINGS §d§l§ka",
            "Count": 1
        },
        {
            "Id": null,
            "ItemName": "§dJaded Helmet of Divan",
            "Tag": "DIVAN_HELMET",
            "ExtraAttributes": {
                "rarity_upgrades": 1,
                "gems": {
                    "JADE_1": "PERFECT",
                    "JADE_0": "PERFECT",
                    "unlocked_slots": [
                        "AMBER_0",
                        "AMBER_1",
                        "JADE_0",
                        "JADE_1",
                        "TOPAZ_0"
                    ],
                    "AMBER_0": "PERFECT",
                    "AMBER_1": "PERFECT",
                    "TOPAZ_0": "PERFECT"
                },
                "modifier": "jaded",
                "uid": "3c6846979240",
                "uuid": "232aa3f5-ff5e-4538-88bc-3c6846979240",
                "timestamp": 1715884465343,
                "tier": 8
            },
            "Enchantments": {
                "rejuvenate": 5,
                "protection": 5,
                "growth": 5,
                "respiration": 3,
                "aqua_affinity": 1
            },
            "Color": null,
            "Description": "§7Health: §a+175\n§7Defense: §a+150\n§7Mining Speed: §a+340 §9(+60) §d(+200)\n§7Mining Fortune: §a+160 §9(+30) §d(+100)\n§7Pristine: §a+2 §d(+2)\n§7Health Regen: §a+10\n §6[§6⸕§6] §6[§a☘§6] §6[§6⸕§6] §6[§a☘§6] §6[§e✧§6]\n\n§9Aqua Affinity I\n§7Increases your underwater mining\n§7rate.\n§9Growth V\n§7Grants §a+75 §c❤ Health§7.\n§9Protection V\n§7Grants §a+20 §a❈ Defense§7.\n§9Rejuvenate V\n§7Grants §c+10❣ Health Regen§7.\n§9Respiration III\n§7Extends your underwater breathing\n§7time by §a45s§7.\n\n§d§l§ka§r §d§l§d§lMYTHIC HELMET §d§l§ka",
            "Count": 1
        }
    ],
    "Received": [
        {
            "Id": null,
            "ItemName": "§61B coins",
            "Tag": null,
            "ExtraAttributes": null,
            "Enchantments": null,
            "Color": null,
            "Description": "§7Lump-sum amount\n\n§6Total Coins Offered:\n§71.3B\n§8(1,300,000,000)",
            "Count": 10
        },
        {
            "Id": null,
            "ItemName": "§6300M coins",
            "Tag": null,
            "ExtraAttributes": null,
            "Enchantments": null,
            "Color": null,
            "Description": "§7Lump-sum amount\n\n§6Total Coins Offered:\n§71.3B\n§8(1,300,000,000)",
            "Count": 3
        }
    ],
    "OtherSide": "Lexingtoni",
    "TimeStamp": "2024-09-16T10:09:20.5472495Z"
    }
    """;

    [Test]
    public async Task MultiItemTradeSellEachItemStoredAsSeparateFlip()
    {
        // Arrange
        var trade = JsonConvert.DeserializeObject<Models.TradeModel>(ThreeItemTrade);
        trade.TimeStamp = DateTime.UtcNow;
        var savedFlips = new List<PastFlip>();
        
        // Mock all required services
        var mockSniperClient = new Mock<ISniperClient>();
        mockSniperClient.Setup(s => s.GetPrices(It.IsAny<IEnumerable<SaveAuction>>()))
            .ReturnsAsync(new List<Sniper.Client.Model.PriceEstimate>
            {
                new (){Median = 55951748}, 
                new (){Median = 52680000}, 
                new (){Median = 57628256}  
            });
        
        var mockRepresentationConverter = new RepresentationConverter(
            NullLogger<RepresentationConverter>.Instance, 
            mockSniperClient.Object);
        
        var mockFlipStorageService = new Mock<FlipStorageService>(null, null, null);
        mockFlipStorageService.Setup(x => x.SaveFlip(It.IsAny<PastFlip>()))
            .Callback<PastFlip>(flip => savedFlips.Add(flip))
            .Returns(Task.CompletedTask);
        mockFlipStorageService.Setup(x => x.GetFlips(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<PastFlip>());
        
        var mockProfitChangeService = new Mock<ProfitChangeService>(null, null, null, null, null, null, null, null);
        mockProfitChangeService.Setup(x => x.GetChanges(It.IsAny<SaveAuction>(), It.IsAny<SaveAuction>()))
            .ReturnsAsync(new List<PastFlip.ProfitChange>());
        
        var mockTransactionApi = new Mock<PlayerState.Client.Api.ITransactionApi>();
        mockTransactionApi.Setup(x => x.TransactionUuidItemIdPostAsync(It.IsAny<List<Guid>>(), It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, List<long>>());
        
        var mockAuctionsApi = new Mock<Api.Client.Api.IAuctionsApi>();
        // Map UIDs from trade items to purchase UUIDs (without dashes for GetId compatibility)
        var uidToPurchaseUuid = new Dictionary<string, string>
        {
            {"6c637a2f2348", "1267927d5ab34def93416c637a2f2348"}, // Leggings
            {"cdfb42282c37", "a61dcf9372ac47aa98e8cdfb42282c37"}, // Boots
            {"56620299aee5", "d13b0ccf615c4d9093db56620299aee5"}  // Helmet
        };
        
        mockAuctionsApi.Setup(x => x.ApiAuctionsUidsSoldPostWithHttpInfoAsync(It.IsAny<Api.Client.Model.InventoryBatchLookup>(), It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((Api.Client.Model.InventoryBatchLookup lookup, int operationIndex, System.Threading.CancellationToken ct) =>
            {
                var data = new Dictionary<string, List<Api.Client.Model.ItemSell>>();
                foreach (var uid in lookup.Uuids)
                {
                    if (uidToPurchaseUuid.TryGetValue(uid, out var purchaseUuid))
                    {
                        data[uid] = new List<Api.Client.Model.ItemSell>
                        {
                            new Api.Client.Model.ItemSell
                            {
                                Uuid = purchaseUuid,
                                Buyer = "8fb6da3fe4ba4530bcf58c1c10740b49",
                                Timestamp = DateTime.UtcNow.AddDays(-10)
                            }
                        };
                    }
                }
                
                return new Api.Client.Client.ApiResponse<Dictionary<string, List<Api.Client.Model.ItemSell>>>(
                    System.Net.HttpStatusCode.OK,
                    null,
                    data);
            });
        
        // Mock GetAuction call for each purchase
        // create inverse mapping purchaseUuid -> uid so GetAuction mock can include the correct uid
        var purchaseToUid = uidToPurchaseUuid.ToDictionary(kv => kv.Value, kv => kv.Key);
        mockAuctionsApi.Setup(x => x.ApiAuctionAuctionUuidGetWithHttpInfoAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .Returns((string uuid, int operationIndex, System.Threading.CancellationToken ct) =>
            {
                // Return different auction based on which UUID is requested
                var (tag, itemName) = uuid switch
                {
                    "1267927d5ab34def93416c637a2f2348" => ("DIVAN_LEGGINGS", "§dJaded Leggings of Divan"),
                    "a61dcf9372ac47aa98e8cdfb42282c37" => ("DIVAN_BOOTS", "§dJaded Boots of Divan"),
                    "d13b0ccf615c4d9093db56620299aee5" => ("DIVAN_HELMET", "§dJaded Helmet of Divan"),
                    _ => ("TEST_ITEM", "§fTest Item")
                };

                var uidValue = purchaseToUid.TryGetValue(uuid, out var v) ? v : "testuid-" + uuid.Substring(0, 6);
                var auction = new SaveAuction
                {
                    Uuid = uuid,
                    Tag = tag,
                    ItemName = itemName,
                    HighestBidAmount = 45000000, // Each bought for ~45M
                    End = DateTime.UtcNow.AddDays(-15),
                    AuctioneerId = "somesellerid",
                    FlatenedNBT = new() { { "uid", uidValue } },
                    Bids = new() { new() { Bidder = "8fb6da3fe4ba4530bcf58c1c10740b49", Amount = 45000000 } }
                };
                var json = JsonConvert.SerializeObject(auction);

                var response = new Api.Client.Client.ApiResponse<Api.Client.Model.ColorSaveAuction>(
                    System.Net.HttpStatusCode.OK,
                    new Api.Client.Client.Multimap<string, string>(),
                    null,
                    json
                );
                return Task.FromResult(response);
            });
        
        
        var mockSettingsApi = new Mock<Settings.Client.Api.ISettingsApi>();
        
        var trackerService = new TrackerService(
            null,
            NullLogger<TrackerService>.Instance,
            mockAuctionsApi.Object,
            null, // FlipSumaryEventProducer can be null
            null, // scopeFactory can be null, will just skip database finder lookup
            mockProfitChangeService.Object,
            mockFlipStorageService.Object,
            new ActivitySource("test"),
            null,
            null,
            mockSettingsApi.Object,
            null,
            mockTransactionApi.Object,
            mockRepresentationConverter
        );
        
        // Act
        await trackerService.AddTrades(new[] { trade });
        
        // Assert
        savedFlips.Should().HaveCount(3, "each item in the multi-item trade should be stored as a separate flip");
        
        savedFlips.Should().Contain(f => f.ItemTag == "DIVAN_LEGGINGS", "leggings should be saved");
        savedFlips.Should().Contain(f => f.ItemTag == "DIVAN_BOOTS", "boots should be saved");
        savedFlips.Should().Contain(f => f.ItemTag == "DIVAN_HELMET", "helmet should be saved");
        
        savedFlips.Should().OnlyContain(f => f.Flipper == Guid.Parse("8fb6da3fe4ba4530bcf58c1c10740b49"));
        // Ensure each saved flip has a distinct item uid so Cassandra clustering key won't overwrite entries
        var uidCounts = savedFlips.GroupBy(f => f.Uid).ToDictionary(g => g.Key, g => g.Count());
        var duplicates = uidCounts.Where(kv => kv.Value > 1).ToList();
        if (duplicates.Any())
        {
            var msg = "Duplicate UIDs found: " + string.Join(", ", duplicates.Select(d => $"{d.Key} (count={d.Value})"));
            Assert.Fail(msg);
        }

        var totalSellPrice = savedFlips.Sum(f => f.SellPrice);
        totalSellPrice.Should().BeInRange(140999990, 141000010);
    }

    private static string ThreeItemTrade = """
    {
        "UserId": "140536",
        "MinecraftUuid": "8fb6da3f-e4ba-4530-bcf5-8c1c10740b49",
        "Spent": [
            {
                "Id": 1186389063216261,
                "ItemName": "§dJaded Leggings of Divan",
                "Tag": "DIVAN_LEGGINGS",
                "ExtraAttributes": {
                    "rarity_upgrades": 1,
                    "uid": "6c637a2f2348",
                    "uuid": "1267927d-5ab3-4def-9341-6c637a2f2348",
                    "timestamp": 1759236287546,
                    "tier": 8
                },
                "Enchantments": null,
                "Color": null,
                "Description": "§7Health: §c+130\n§7Defense: §a+170\n§7Mining Speed: §6+140 §9(+60)\n§7Mining Fortune: §6+60 §9(+30)\n§7Heat Resistance: §c+10\n §8[§8⸕§8] §8[§8☘§8] §8[§8⸕§8] §8[§8☘§8] §8[§8✧§8]\n\n§d§l§ka§r §d§lMYTHIC LEGGINGS §d§l§ka",
                "Count": 1
            },
            {
                "Id": 1186590488451261,
                "ItemName": "§dJaded Boots of Divan",
                "Tag": "DIVAN_BOOTS",
                "ExtraAttributes": {
                    "rarity_upgrades": 1,
                    "gems": {
                        "JADE_1": "ROUGH",
                        "JADE_0": "ROUGH",
                        "unlocked_slots": ["AMBER_0", "AMBER_1", "JADE_0", "JADE_1", "TOPAZ_0"],
                        "AMBER_0": "ROUGH",
                        "AMBER_1": "ROUGH",
                        "TOPAZ_0": "ROUGH"
                    },
                    "uid": "cdfb42282c37",
                    "uuid": "a61dcf93-72ac-47aa-98e8-cdfb42282c37",
                    "timestamp": 1750765097654,
                    "tier": 8
                },
                "Enchantments": {
                    "depth_strider": 3,
                    "feather_falling": 5,
                    "growth": 5
                },
                "Color": null,
                "Description": "§7Health: §c+155\n§7Defense: §a+110\n§7Mining Speed: §6+188 §9(+60) §d(+48)\n§7Pristine: §5+0.4 §d(+0.4)\n§7Mining Fortune: §6+84 §9(+30) §d(+24)\n§7Heat Resistance: §c+10\n §f[§6⸕§f] §f[§a☘§f] §f[§6⸕§f] §f[§a☘§f] §f[§e✧§f]\n\n§9Depth Strider III\n§7Reduces how much you are slowed in\n§7the water by §a100%§7.\n§9Feather Falling V\n§7Increases how high you can fall\n§7before taking fall damage by §a5§7 and\n§7reduces fall damage by §a25%§7.\n§9Growth V\n§7Grants §a+75 §c❤ Health§7.\n\n§d§l§ka§r §d§lMYTHIC BOOTS §d§l§ka",
                "Count": 1
            },
            {
                "Id": 1186590570989261,
                "ItemName": "§dJaded Helmet of Divan",
                "Tag": "DIVAN_HELMET",
                "ExtraAttributes": {
                    "rarity_upgrades": 1,
                    "hot_potato_count": 10,
                    "gems": {
                        "JADE_1": "ROUGH",
                        "JADE_0": "ROUGH",
                        "unlocked_slots": ["AMBER_0", "AMBER_1", "JADE_0", "JADE_1", "TOPAZ_0"],
                        "AMBER_0": "ROUGH",
                        "AMBER_1": "ROUGH",
                        "TOPAZ_0": "ROUGH"
                    },
                    "uid": "56620299aee5",
                    "uuid": "d13b0ccf-615c-4d90-93db-56620299aee5",
                    "timestamp": 1629573420000,
                    "tier": 8
                },
                "Enchantments": {
                    "ultimate_last_stand": 5,
                    "protection": 5,
                    "growth": 5,
                    "respiration": 3,
                    "aqua_affinity": 1
                },
                "Color": null,
                "Description": "§7Health: §c+215 §e(+40)\n§7Defense: §a+170 §e(+20)\n§7Mining Speed: §6+188 §9(+60) §d(+48)\n§7Pristine: §5+0.4 §d(+0.4)\n§7Mining Fortune: §6+84 §9(+30) §d(+24)\n§7Heat Resistance: §c+10\n§7Respiration: §3+45\n §f[§6⸕§f] §f[§a☘§f] §f[§6⸕§f] §f[§a☘§f] §f[§e✧§f]\n\n§9§d§lLast Stand V\n§7Gain §a+25% §a❈ Defense §7when hit while\n§7below §c40%❤§7.\n§9Aqua Affinity I\n§7Increases your underwater mining\n§7rate.\n§9Growth V\n§7Grants §a+75 §c❤ Health§7.\n§9Protection V\n§7Grants §a+20 §a❈ Defense§7.\n§9Respiration III\n§7Grants §3+45⚶ Respiration§7, which\n§7increases the amount of time you\n§7can stay under water.\n\n§d§l§ka§r §d§lMYTHIC HELMET §d§l§ka",
                "Count": 1
            }
        ],
        "Received": [
            {
                "Id": null,
                "ItemName": "§6141M coins",
                "Tag": null,
                "ExtraAttributes": null,
                "Enchantments": null,
                "Color": null,
                "Description": "§7Lump-sum amount\n\n§6Total Coins Offered:\n§7141M\n§8(141,000,000)",
                "Count": 2
            }
        ],
        "OtherSide": "imjustcake",
        "TimeStamp": "2025-10-05T09:58:38.4644449Z"
    }
    """;
}