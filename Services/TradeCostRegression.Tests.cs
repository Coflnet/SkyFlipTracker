using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Transaction = Coflnet.Sky.PlayerState.Client.Model.Transaction;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>Trade accounting regressions using sanitized operator evidence.</summary>
public class TradeCostRegressionTests
{
    private static readonly Guid Player = Guid.Empty;
    private static readonly DateTime Bought = new(2026, 9, 9, 22, 31, 26, 967);
    private static readonly DateTime Sold = new(2026, 9, 9, 22, 31, 55, 738);
    private const long Coins = 1000001;

    /// <summary>Unknown barter costs retain sales without fabricated profits.</summary>
    [Test]
    public async Task UnresolvedDragonConsiderationPreservesSalesWithoutInventingProfit()
    {
        // Exact sanitized operator window. Generated local keys below are routing scaffolding,
        // not identities or independently measured per-item sale prices.
        var evidence = JObject.Parse(Evidence);
        var transactions = evidence["transactions"].Select(t => Entry(
            t["item"].ToString() == "coins" ? Coins : 2000000 + int.Parse(t["item"].ToString()[5..]),
            (int)t["type"], (long)t["amount"], (DateTime)t["timeStamp"])).ToList();
        var api = Transactions(transactions);
        var items = new Mock<PlayerState.Client.Api.IItemsApi>();
        var converter = new RepresentationConverter(NullLogger<RepresentationConverter>.Instance, null);
        var lookup = new Dictionary<string, List<long>>();
        var sells = IncidentSales(evidence, items, converter, lookup);
        api.Setup(x => x.TransactionUuidItemIdPostAsync(It.IsAny<List<Guid>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lookup);
        var auctions = new Mock<Api.Client.Api.IAuctionsApi>();
        auctions.Setup(x => x.ApiAuctionsUidsSoldPostWithHttpInfoAsync(It.IsAny<Api.Client.Model.InventoryBatchLookup>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Api.Client.Client.ApiResponse<Dictionary<string, List<Api.Client.Model.ItemSell>>>(
                System.Net.HttpStatusCode.OK, null, new Dictionary<string, List<Api.Client.Model.ItemSell>>()));
        var saved = new System.Collections.Concurrent.ConcurrentBag<PastFlip>();
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(x => x.SaveFlip(It.IsAny<PastFlip>())).Callback<PastFlip>(saved.Add).Returns(Task.CompletedTask);
        var changes = new Mock<ProfitChangeService>(null, null, null, null, null, null, null, null, null);
        changes.Setup(x => x.GetChanges(It.IsAny<SaveAuction>(), It.IsAny<SaveAuction>()))
            .ReturnsAsync(new List<PastFlip.ProfitChange>());
        var tracker = new TrackerService(null, NullLogger<TrackerService>.Instance, auctions.Object,
            null, null, changes.Object, storage.Object, new ActivitySource("trade-regression"),
            null, null, new Mock<Settings.Client.Api.ISettingsApi>().Object, items.Object, api.Object, converter);

        await tracker.IndexCassandra(sells);

        Assert.That(saved.Count, Is.EqualTo(3));
        Assert.That(saved.Select(f => f.SellPrice), Is.EquivalentTo(new long[] { 834733248, 990632832, 1134633856 }));
        foreach (var flip in saved)
        {
            Assert.That(flip.PurchaseTime, Is.EqualTo(Bought));
            Assert.That(flip.SellTime, Is.EqualTo(Sold));
            Assert.That(flip.PurchaseCost, Is.EqualTo(-1), "eleven outgoing items include unresolved consideration, not a free gift");
            Assert.That(flip.Profit, Is.Zero);
            Assert.That((int)flip.Flags & 8, Is.EqualTo(8));
            Assert.That(flip.ProfitChanges.Single().Label, Does.Contain("unknown"));
        }
        changes.Verify(x => x.GetChanges(It.IsAny<SaveAuction>(), It.IsAny<SaveAuction>()), Times.Never);
    }

    /// <summary>Checks conventional consideration and allocation independently of the incident.</summary>
    [TestCase(3000, false, false, 100)]
    [TestCase(0, false, false, 0)]
    [TestCase(3000, true, false, -1)]
    [TestCase(0, true, false, -1)]
    [TestCase(3000, true, true, 300)]
    [TestCase(0, true, true, 200)]
    [TestCase(-3000, true, true, 100)]
    public async Task CoinAndItemConsideration(long coins, bool outgoing, bool knownBasis, long expected)
    {
        // Conventional unit cases, not claims about the missing evidence item.
        var rows = new List<Transaction> { Entry(2000008, 33, 1, Bought), Entry(2000009, 33, 1, Bought), Entry(2000014, 33, 1, Bought) };
        if (coins != 0) rows.Add(Entry(Coins, coins > 0 ? 34 : 33, Math.Abs(coins), Bought));
        if (outgoing) rows.Add(Entry(2000001, 34, 1, Bought));
        if (knownBasis)
        {
            rows.Add(Entry(2000001, 33, 1, Bought.AddDays(-1)));
            rows.Add(Entry(Coins, 34, 6000, Bought.AddDays(-1)));
        }
        rows.Add(Entry(2000008, 34, 1, Sold));
        rows.Add(Entry(Coins, 33, 30000000000, Sold));
        var tracker = CreateTracker(Transactions(rows).Object);
        var result = await TradeValue(tracker, rows.Where(t => t.ItemId == 2000008).ToList(), Sold);
        Assert.That(result.cost, Is.EqualTo(expected));
        Assert.That(result.count, Is.EqualTo(3));
    }

    /// <summary>Unknown costs bypass notification processing.</summary>
    [Test]
    public async Task UnknownCostCannotGenerateMissedFlipClaim()
    {
        var tracker = CreateTracker(Transactions(new()).Object);
        var method = typeof(TrackerService).GetMethod("MissedFlip", BindingFlags.Instance | BindingFlags.NonPublic);
        // A null scope factory deliberately fails if notification processing is reached.
        await (Task)method.Invoke(tracker, new object[] { new PastFlip { PurchaseCost = -1, Profit = 990632831 }, "missed", new SaveAuction() });
    }

    /// <summary>Only the seller's acquisition before sale can establish cost.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrFutureAcquisitionIsUnknown(bool future)
    {
        var history = new List<Transaction> { Entry(2000008, 34, 1, Sold) };
        if (future) history.Add(Entry(2000008, 33, 1, Sold.AddDays(1)));
        var result = await TradeValue(CreateTracker(Transactions(history).Object), history, Sold);
        Assert.That(result.cost, Is.EqualTo(-1));
    }

    /// <summary>A missing outgoing record is not a zero-cost item.</summary>
    [Test]
    public async Task UnavailableOutgoingHistoryIsUnknown()
    {
        var rows = new List<Transaction> { Entry(2000008, 33, 1, Bought), Entry(2000001, 34, 1, Bought) };
        var api = Transactions(rows);
        api.Setup(x => x.TransactionItemItemIdGetAsync(2000001, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PlayerState.Client.Client.ApiException(404, "unavailable"));
        var result = await TradeValue(CreateTracker(api.Object), rows.Take(1).ToList(), Sold);
        Assert.That(result.cost, Is.EqualTo(-1));
    }

    /// <summary>Trade-stage state replaces linked auction state at the acquisition time.</summary>
    [Test]
    public async Task LinkedAuctionUsesUnknownTradeCostAndAcquisitionState()
    {
        var state = JObject.Parse(Evidence)["items"]["item_8"].ToObject<PlayerState.Client.Model.Item>();
        state.Id = 2000008;
        var itemUuid = Guid.Empty.ToString();
        state.ExtraAttributes["uuid"] = itemUuid;
        var rows = new List<Transaction>
        {
            Entry(2000008, 34, 1, Sold), Entry(Coins, 33, 30000000000, Sold),
            Entry(2000008, 33, 1, Bought), Entry(2000001, 34, 1, Bought)
        };
        var items = new Mock<PlayerState.Client.Api.IItemsApi>();
        items.Setup(x => x.ApiItemsFindUuidPostAsync(It.IsAny<List<PlayerState.Client.Model.ItemIdSearch>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlayerState.Client.Model.Item> { state });
        var converter = new RepresentationConverter(NullLogger<RepresentationConverter>.Instance, null);
        var tracker = new TrackerService(null, NullLogger<TrackerService>.Instance, null, null, null, null,
            null, new ActivitySource("linked-trade"), null, null, null, items.Object, Transactions(rows).Object, converter);
        var buy = new ApiSaveAuction
        {
            Bids = new() { new() { Bidder = "prior-owner" } },
            FlatenedNBT = new() { { "exp", "0" } }, End = Bought.AddDays(-10)
        };
        var sell = converter.FromItemRepresent(state);
        sell.AuctioneerId = Player.ToString("N");
        sell.Uuid = Guid.Empty.ToString("N");
        sell.End = Sold;
        var method = typeof(TrackerService).GetMethod("CheckTrade", BindingFlags.Instance | BindingFlags.NonPublic);
        var result = await (Task<(FlipFlags flags, PastFlip.ProfitChange change)>)method.Invoke(tracker, new object[] { buy, sell });
        Assert.That(buy.HighestBidAmount, Is.EqualTo(-1));
        Assert.That(buy.End, Is.EqualTo(Bought));
        Assert.That(buy.FlatenedNBT["exp"], Is.EqualTo(sell.FlatenedNBT["exp"]));
        Assert.That(buy.FlatenedNBT["heldItem"], Is.EqualTo("POIGNANT_LUCKY_CLOVER"));
        Assert.That(result.change.Label, Does.Contain("unknown"));
    }

    private static List<SaveAuction> IncidentSales(JObject evidence,
        Mock<PlayerState.Client.Api.IItemsApi> items, RepresentationConverter converter,
        Dictionary<string, List<long>> lookup)
    {
        var sells = new List<SaveAuction>();
        int[] labels = [14, 8, 9];
        for (var i = 0; i < labels.Length; i++)
        {
            var id = 2000000 + labels[i];
            var state = evidence["items"][$"item_{labels[i]}"].ToObject<PlayerState.Client.Model.Item>();

            // Distinct dummy keys retain the association through the normal purchase matching path.
            var uuid = $"00000000-0000-0000-0000-{id:x12}";
            state.ExtraAttributes["uuid"] = uuid;
            state.ExtraAttributes["uid"] = uuid.Split('-').Last();
            items.Setup(x => x.ApiItemsIdGetAsync(id, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(state);
            lookup[uuid] = new() { id };
            var sell = converter.FromItemRepresent(state);
            sell.Uuid = Guid.Empty.ToString("N");
            sell.UId = id;
            sell.AuctioneerId = Player.ToString("N");
            sell.End = Sold;
            sell.HighestBidAmount = (long)evidence["recorded_sale_allocations"][i];
            sells.Add(sell);
        }
        return sells;
    }

    private static async Task<(int count, long cost, List<Transaction> items)> TradeValue(
        TrackerService tracker, List<Transaction> history, DateTime before)
    {
        var method = typeof(TrackerService).GetMethod("GetTradeValue", BindingFlags.Instance | BindingFlags.NonPublic);
        object[] args = method.GetParameters().Length == 2
            ? new object[] { history, Player }
            : new object[] { history, Player, before };
        return await (Task<(int, long, List<Transaction>)>)method.Invoke(tracker, args);
    }

    private static TrackerService CreateTracker(PlayerState.Client.Api.ITransactionApi api)
    {
        return new TrackerService(null, NullLogger<TrackerService>.Instance, null, null, null, null,
            null, new ActivitySource("trade-cost"), null, null, null, null, api, null);
    }

    private static Transaction Entry(long item, int type, long amount, DateTime time)
    {
        return JsonConvert.DeserializeObject<Transaction>(JsonConvert.SerializeObject(new
        {
            ItemId = item, Type = type, Amount = amount, TimeStamp = time, PlayerUuid = Player
        }));
    }

    private static Mock<PlayerState.Client.Api.ITransactionApi> Transactions(List<Transaction> rows)
    {
        var api = new Mock<PlayerState.Client.Api.ITransactionApi>();
        api.Setup(x => x.TransactionItemItemIdGetAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id, int page, int index, CancellationToken token) => rows.Where(t => t.ItemId == id).OrderByDescending(t => t.TimeStamp).ToList());
        api.Setup(x => x.TransactionPlayerPlayerUuidGetAsync(It.IsAny<Guid>(), 1, It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid player, int seconds, DateTime? time, int index, CancellationToken token) => rows.Where(t => t.TimeStamp == time).ToList());
        return api;
    }

    private const string Evidence = """
{
  "provenance": "Bounded operator reads of the exact 40-second player-state transaction window and item records. Item identifiers replaced with stable labels. Coin transaction amounts use tenths of a coin. No purchase coin payment exists; eleven items were given instead. One item record returned 404.",
  "transactions": [
    {
      "item": "item_14",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:55.738"
    },
    {
      "item": "item_9",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:55.738"
    },
    {
      "item": "item_8",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:55.738"
    },
    {
      "item": "coins",
      "type": 33,
      "amount": 30000000000,
      "timeStamp": "2026-09-09T22:31:55.738"
    },
    {
      "item": "item_14",
      "type": 33,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_13",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_12",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_11",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_10",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_9",
      "type": 33,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_8",
      "type": 33,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_7",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_6",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_5",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_4",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_3",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_2",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    },
    {
      "item": "item_1",
      "type": 34,
      "amount": 1,
      "timeStamp": "2026-09-09T22:31:26.967"
    }
  ],
  "items": {
    "item_1": {
      "unavailable": true
    },
    "item_2": {
      "tag": "INFERNAL_CRIMSON_HELMET",
      "itemName": "Ancient Infernal Crimson Helmet",
      "count": 1,
      "enchantments": {
        "growth": 6,
        "protection": 6,
        "rejuvenate": 5,
        "strong_mana": 4,
        "transylvanian": 5,
        "ultimate_habanero_tactics": 5
      },
      "extraAttributes": {
        "rarity_upgrades": 1,
        "hot_potato_count": 15,
        "gems": {
          "COMBAT_0": "PERFECT",
          "unlocked_slots": [
            "COMBAT_0",
            "COMBAT_1"
          ],
          "COMBAT_1_gem": "JASPER",
          "COMBAT_0_gem": "JASPER",
          "COMBAT_1": "PERFECT"
        },
        "modifier": "ancient",
        "boss_tier": 4,
        "timestamp": 1774105610102,
        "tier": 8
      }
    },
    "item_3": {
      "tag": "INFERNAL_CRIMSON_BOOTS",
      "itemName": "Ancient Infernal Crimson Boots",
      "count": 1,
      "enchantments": {
        "feather_falling": 10,
        "ferocious_mana": 4,
        "growth": 6,
        "protection": 6,
        "rejuvenate": 5,
        "sugar_rush": 3,
        "ultimate_habanero_tactics": 5
      },
      "extraAttributes": {
        "rarity_upgrades": 1,
        "hot_potato_count": 15,
        "gems": {
          "COMBAT_0": "PERFECT",
          "unlocked_slots": [
            "COMBAT_0",
            "COMBAT_1"
          ],
          "COMBAT_1_gem": "JASPER",
          "COMBAT_0_gem": "JASPER",
          "COMBAT_1": "PERFECT"
        },
        "modifier": "ancient",
        "boss_tier": 0,
        "timestamp": 1653064560000,
        "tier": 8
      }
    },
    "item_4": {
      "tag": "PET_BLUE_WHALE",
      "itemName": "§7[Lvl 100] §6Blue Whale",
      "count": 1,
      "enchantments": {},
      "extraAttributes": {
        "petInfo": {
          "type": "BLUE_WHALE",
          "active": false,
          "exp": 25617519.58998397,
          "tier": "LEGENDARY",
          "hideInfo": false,
          "heldItem": "CROCHET_TIGER_PLUSHIE",
          "candyUsed": 0,
          "hideRightClick": true,
          "noMove": false,
          "extraData": {},
          "petSoulbound": false
        },
        "timestamp": 1788209670406,
        "tier": 5
      }
    },
    "item_5": {
      "tag": "PERSONAL_COMPACTOR_7000",
      "itemName": "Personal Compactor 7000",
      "count": 1,
      "enchantments": {},
      "extraAttributes": {
        "PERSONAL_DELETOR_ACTIVE": 1,
        "personal_compact_0": "ENCHANTED_RED_MUSHROOM",
        "personal_compact_2": "ENCHANTED_HUGE_MUSHROOM_2",
        "personal_compact_1": "ENCHANTED_BROWN_MUSHROOM",
        "personal_compact_8": "ENCHANTED_FIG_LOG",
        "personal_compact_7": "MUTANT_NETHER_STALK",
        "personal_compact_9": "ENCHANTED_MANGROVE_LOG",
        "personal_compact_4": "ENCHANTED_MELON",
        "personal_compact_3": "ENCHANTED_HUGE_MUSHROOM_1",
        "personal_compact_6": "ENCHANTED_NETHER_STALK",
        "timestamp": 1788060147956,
        "personal_compact_5": "ENCHANTED_MELON_BLOCK",
        "tier": 5
      }
    },
    "item_6": {
      "tag": "MELON_DICER_3",
      "itemName": "Bountiful Melon Dicer Mk. III",
      "count": 1,
      "enchantments": {
        "cultivating": 10,
        "dedication": 3,
        "efficiency": 5,
        "sunder": 6,
        "turbo_melon": 5
      },
      "extraAttributes": {
        "rarity_upgrades": 1,
        "gems": {
          "unlocked_slots": [
            "PERIDOT_0",
            "PERIDOT_1",
            "PERIDOT_2"
          ],
          "PERIDOT_1": {
            "quality": "FLAWLESS"
          },
          "PERIDOT_2": {
            "quality": "FLAWLESS"
          },
          "PERIDOT_0": {
            "quality": "FLAWLESS"
          }
        },
        "levelable_exp": 40665658.75,
        "farmed_cultivating": 57638032,
        "levelable_lvl": 40,
        "modifier": "bountiful",
        "timestamp": 1697281860000,
        "tier": 5
      }
    },
    "item_7": {
      "tag": "DCTR_SPACE_HELM",
      "itemName": "Space Helmet",
      "count": 1,
      "enchantments": {},
      "extraAttributes": {
        "rarity_upgrades": 1,
        "timestamp": 1725830167401,
        "tier": 7
      }
    },
    "item_8": {
      "tag": "PET_ROSE_DRAGON",
      "itemName": "§7[Lvl 200] §8[§69§4✦§8] §6Rose Dragon",
      "count": 1,
      "enchantments": {},
      "extraAttributes": {
        "petInfo": {
          "type": "ROSE_DRAGON",
          "active": false,
          "exp": 232780961.2219762,
          "tier": "LEGENDARY",
          "hideInfo": false,
          "heldItem": "POIGNANT_LUCKY_CLOVER",
          "candyUsed": 0,
          "skin": "ROSE_DRAGON_PETAL",
          "hideRightClick": false,
          "noMove": false,
          "extraData": {
            "favorite_rose_dragon": 11
          },
          "petSoulbound": false
        },
        "timestamp": 1788933154474,
        "tier": 5
      }
    },
    "item_9": {
      "tag": "PET_GOLDEN_DRAGON",
      "itemName": "§7[Lvl 200] §6Golden Dragon§4 ✦",
      "count": 1,
      "enchantments": {},
      "extraAttributes": {
        "petInfo": {
          "type": "GOLDEN_DRAGON",
          "active": false,
          "exp": 658635856.2034823,
          "tier": "LEGENDARY",
          "hideInfo": false,
          "heldItem": "HEPHAESTUS_RELIC",
          "candyUsed": 0,
          "skin": "GOLDEN_DRAGON_SWAP_PLUSHIE",
          "hideRightClick": false,
          "noMove": false,
          "extraData": {
            "favorite_plushie_gdragon": 0
          },
          "petSoulbound": false
        },
        "timestamp": 1788989531548,
        "tier": 5
      }
    },
    "item_10": {
      "tag": "TERMINATOR",
      "itemName": "§dPrecise Terminator §6✪✪✪✪✪§c➌",
      "count": 1,
      "enchantments": {
        "aiming": 5,
        "chance": 4,
        "cubism": 5,
        "dragon_hunter": 5,
        "flame": 2,
        "impaling": 5,
        "infinite_quiver": 10,
        "overload": 5,
        "piercing": 1,
        "power": 6,
        "snipe": 3,
        "tabasco": 3,
        "toxophilite": 10,
        "ultimate_soul_eater": 5,
        "vicious": 3
      },
      "extraAttributes": {
        "rarity_upgrades": 1,
        "hot_potato_count": 15,
        "runes": {
          "GOLDEN": 2
        },
        "dungeon_item": 1,
        "modifier": "precise",
        "art_of_war_count": 1,
        "upgrade_level": 8,
        "toxophilite_combat_xp": 42399148.09677684,
        "timestamp": 1782834018242,
        "tier": 8
      }
    },
    "item_11": {
      "tag": "SPEED_WITHER_BOOTS",
      "itemName": "§b✿ §dAncient Maxor's Boots §6✪✪✪✪✪§c➌",
      "count": 1,
      "enchantments": {
        "depth_strider": 3,
        "feather_falling": 5,
        "growth": 5,
        "protection": 5,
        "rejuvenate": 5,
        "sugar_rush": 3,
        "ultimate_last_stand": 5
      },
      "extraAttributes": {
        "rarity_upgrades": 1,
        "hot_potato_count": 10,
        "modifier": "ancient",
        "upgrade_level": 8,
        "timestamp": 1782191484020,
        "dye_item": "DYE_OASIS",
        "tier": 8
      }
    },
    "item_12": {
      "tag": "POWER_WITHER_LEGGINGS",
      "itemName": "§b✿ §dAncient Necron's Leggings §6✪✪✪✪✪§c➌",
      "count": 1,
      "enchantments": {
        "growth": 5,
        "protection": 5,
        "rejuvenate": 5,
        "ultimate_last_stand": 5
      },
      "extraAttributes": {
        "rarity_upgrades": 1,
        "hot_potato_count": 10,
        "modifier": "ancient",
        "upgrade_level": 8,
        "timestamp": 1711412872376,
        "dye_item": "DYE_OASIS",
        "tier": 8
      }
    },
    "item_13": {
      "tag": "SKELETON_MASTER_CHESTPLATE",
      "itemName": "§b✿ §dAncient Skeleton Master Chestplate §6✪✪✪✪✪§c➋",
      "count": 1,
      "enchantments": {
        "growth": 6,
        "protection": 6,
        "rejuvenate": 5,
        "ultimate_last_stand": 5
      },
      "extraAttributes": {
        "dungeon_skill_req": "CATACOMBS:32",
        "rarity_upgrades": 1,
        "hot_potato_count": 15,
        "baseStatBoostPercentage": 50,
        "modifier": "ancient",
        "upgrade_level": 7,
        "item_tier": 8,
        "timestamp": 1785206574250,
        "dye_item": "DYE_OASIS",
        "tier": 8
      }
    },
    "item_14": {
      "tag": "PET_GOLDEN_DRAGON",
      "itemName": "§7[Lvl 154] §6Golden Dragon",
      "count": 1,
      "enchantments": {},
      "extraAttributes": {
        "petInfo": {
          "type": "GOLDEN_DRAGON",
          "active": false,
          "exp": 128373676.72556767,
          "tier": "LEGENDARY",
          "hideInfo": false,
          "heldItem": "PET_ITEM_COMBAT_SKILL_BOOST_EPIC",
          "candyUsed": 0,
          "hideRightClick": false,
          "noMove": false,
          "extraData": {},
          "petSoulbound": false
        },
        "timestamp": 1788989532413,
        "tier": 5
      }
    }
  },
  "recorded_sale_allocations": [
    834733248,
    990632832,
    1134633856
  ],
  "recorded_purchase_costs": [
    0,
    0,
    0
  ],
  "reported_profits": [
    834733247,
    990632831,
    1134633855
  ],
  "reported_percentages": [
    38,
    46,
    52
  ]
}
""";
}
