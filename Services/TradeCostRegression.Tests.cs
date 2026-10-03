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

    /// <summary>The bundle bought for coins charges each item its share of the estimated value, whichever estimate it has.</summary>
    [Test]
    public async Task BundleCostFollowsEstimatedValueShare()
    {
        var bundle = CreateBundle();

        var flips = await TradeFlips(bundle);

        Assert.That(flips["DIVAN_DRILL"].cost, Is.EqualTo(1_369_020_021), "the drill carried 181,250,000, an even sixteenth");
        Assert.That(flips["DIVAN_PENDANT"].cost, Is.EqualTo(440_042_149));
        Assert.That(flips.Values.Sum(f => f.cost), Is.EqualTo(BundleCoins).Within(16));
        // sniper median, the pets' rarity and exp reach it
        var drill = bundle.SniperRequests[0].Single(a => a.Tag == "DIVAN_DRILL");
        Assert.That((drill.Reforge, drill.FlatenedNBT["AMBER_0"], drill.FlatenedNBT["MINING_0_gem"], drill.Enchantments.Count),
            Is.EqualTo((ItemReferences.Reforge.lustrous, "PERFECT", "TOPAZ", 9)), "gems and enchantments are part of the valued item");
        var bal = bundle.SniperRequests[0].Single(a => a.Tag == "PET_BAL");
        Assert.That((bal.Tier, bal.FlatenedNBT["exp"]), Is.EqualTo((Tier.LEGENDARY, "27923968.73310481")));
        // tag-level price: the Bal by rarity and level, the belt once the reforge filter is dropped
        Assert.That(bundle.TagRequests.Where(r => r.tag == "PET_BAL").Select(r => string.Join(",", r.filters.Select(f => f.Key + "=" + f.Value))).Distinct(),
            Is.EqualTo(new[] { "Rarity=LEGENDARY,PetLevel=100" }));
        Assert.That(bundle.TagRequests.Where(r => r.tag == "TITANIUM_BELT").Select(r => string.Join(",", r.filters.Keys)).Distinct(),
            Is.EqualTo(new[] { "Rarity,Reforge", "Rarity" }));
        Assert.That(flips["PET_BAL"].cost, Is.EqualTo(58_672_286));
        Assert.That(flips["TITANIUM_BELT"].cost, Is.EqualTo(19_557_428));
        // even share: mean of the 50M the estimates left uncovered and of an even sixteenth
        Assert.That(flips["PET_ARMADILLO"].cost, Is.EqualTo(113_066_385));
        foreach (var (tag, flip) in flips)
        {
            Assert.That((int)flip.flags & (4 | 16), Is.EqualTo(tag == "PET_ARMADILLO" ? 4 | 16 : 4), tag);
            Assert.That(flip.change.Label, Does.StartWith("Item was traded with other items for about " + flip.cost));
            Assert.That(flip.change.Label.Contains("even share"), Is.EqualTo(tag == "PET_ARMADILLO"), tag);
            Assert.That(flip.change.Amount, Is.Zero);
        }
    }

    /// <summary>The Divan drill without a sniper median takes its tag's price: its sales are stored without rarity, no rarity filter finds them.</summary>
    [Test]
    public async Task ItemWithoutSalesOfItsRarityTakesUnfilteredTagPrice()
    {
        var items = BundleBuy.Select(i => i.Tag == "DIVAN_DRILL" ? i with { Median = 0 } : i).ToArray();
        var bundle = CreateTrade(BundleCoins, items, (tag, filters) => tag == "DIVAN_DRILL" && !filters.ContainsKey("Rarity") ? 1_775_000_000 : 0);

        var flips = await TradeFlips(bundle);

        Assert.That(flips["DIVAN_DRILL"].cost, Is.EqualTo(1_506_493_506), "the drill carried 327,302,302, an even share");
        Assert.That((int)flips["DIVAN_DRILL"].flags & (4 | 16), Is.EqualTo(4));
        Assert.That(flips["DIVAN_PENDANT"].cost, Is.EqualTo(381_927_931), "the pendant carried 522,522,522");
        Assert.That(bundle.TagRequests.Where(r => r.tag == "DIVAN_DRILL").Select(r => string.Join(",", r.filters.Keys)).Distinct(),
            Is.EqualTo(new[] { "Rarity,Reforge", "Rarity", "" }));
        // a pet's price depends on its rarity, it is never asked for without
        Assert.That(bundle.TagRequests.Where(r => r.tag.StartsWith("PET_")).Select(r => r.filters.ContainsKey("Rarity")), Is.All.True);
        Assert.That((int)flips["PET_ARMADILLO"].flags & (4 | 16), Is.EqualTo(4 | 16));
    }

    /// <summary>The bundle left in four later trades, each batch is valued once and its profit is its coins minus its items' cost.</summary>
    [Test]
    public async Task BundleSoldInBatchesIsValuedOncePerBatch()
    {
        var bundle = CreateBundle();
        var saved = new List<PastFlip>();

        foreach (var batch in BundleSells)
        {
            var flips = await IndexSales(bundle, BatchSales(bundle, batch), null);
            Assert.That(flips.Sum(f => f.Profit), Is.EqualTo(batch.coins - flips.Sum(f => f.PurchaseCost)).Within(batch.tags.Length), batch.tags[0]);
            saved.AddRange(flips);
        }

        // the two pets, the belt and the jasper drill were kept, so the batches do not add up to the purchase
        Assert.That(saved.Count, Is.EqualTo(12));
        Assert.That(saved.Single(f => f.ItemTag == "DIVAN_DRILL").PurchaseCost, Is.EqualTo(1_369_020_021));
        Assert.That(saved.Single(f => f.ItemTag == "DIVAN_DRILL").Profit, Is.EqualTo(1_680_000_000 - 1_369_020_021));
        Assert.That(saved.Select(f => (int)f.Flags), Is.All.EqualTo(2 | 4));
        Assert.That(saved.Select(f => f.ProfitChanges.Single().Amount), Is.All.Zero);
        Assert.That(bundle.SniperRequests.Count, Is.EqualTo(BundleSells.Length));
    }

    /// <summary>Only the item without any estimate is stored without profit, summary event or missed-flip claim.</summary>
    [Test]
    public async Task UncertainBundleItemReportsNoProfit()
    {
        // An uninitialized producer and the null scope factory both throw if a summary or notification is attempted.
        var producer = (FlipSumaryEventProducer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FlipSumaryEventProducer));
        var bundle = CreateBundle("SAPPHIRE_CLOAK");

        var cloak = (await IndexSales(bundle, BatchSales(bundle, BundleSells[3]), producer)).Single();
        var armor = await IndexSales(bundle, BatchSales(bundle, BundleSells[0]), null);

        Assert.That(cloak.PurchaseCost, Is.EqualTo(113_197_026));
        Assert.That(cloak.Profit, Is.Zero);
        Assert.That((int)cloak.Flags, Is.EqualTo(2 | 4 | 16));
        Assert.That(cloak.ProfitChanges.Single().Label, Does.Contain("even share"));
        Assert.That(armor.Select(f => (int)f.Flags), Is.All.EqualTo(2 | 4));
        Assert.That(armor.Select(f => f.Profit), Is.All.EqualTo(43_750_000 - 33_539_859));
        var missed = typeof(TrackerService).GetMethod("MissedFlip", BindingFlags.Instance | BindingFlags.NonPublic);
        await (Task)missed.Invoke(CreateTracker(Transactions(new()).Object), new object[]
            { new PastFlip { PurchaseCost = 113_197_026, Profit = 1_883_493_039, Flags = (FlipFlags)16 }, "missed", new SaveAuction() });
    }

    /// <summary>A drill bought together with a pet and sold on the auction house is charged its value share, not half.</summary>
    [Test]
    public async Task TwoItemBuyChargesDrillItsValueShare()
    {
        var trade = CreateTrade(TwoItemCoins, TwoItemBuy, (_, _) => 0);
        var sell = Sale(trade, 0, 249_999_000);
        sell.Uuid = "00000000000000000000000000004000";

        var flip = (await TradeFlips(trade, sell))["TITANIUM_DRILL_3"];

        Assert.That(flip.cost, Is.EqualTo(226_923_076), "the drill carried 147,500,000, an even half");
        Assert.That(sell.HighestBidAmount - flip.cost, Is.EqualTo(23_075_924));
        Assert.That((int)flip.flags & (4 | 16), Is.EqualTo(4));
        Assert.That(flip.change.Amount, Is.Zero);
        // the golem stores no top level tier, rarity and exp come from its petInfo
        var drill = trade.SniperRequests.Single().Single(a => a.Tag == "TITANIUM_DRILL_3");
        Assert.That((drill.FlatenedNBT["AMBER_0"], drill.FlatenedNBT["JADE_0"], drill.Enchantments.Count), Is.EqualTo(("FLAWLESS", "FLAWLESS", 6)));
        var golem = trade.SniperRequests.Single().Single(a => a.Tag == "PET_GLACITE_GOLEM");
        Assert.That((golem.Tier, golem.FlatenedNBT["exp"]), Is.EqualTo((Tier.EPIC, "392338765.75743145")));
        Assert.That(trade.TagRequests, Is.Empty);
    }

    /// <summary>A sniper that answers with an error does not stop the purchase from being tracked, the tag-level prices take over.</summary>
    [Test]
    public async Task TwoItemBuySurvivesSniperFailure()
    {
        var trade = CreateTrade(TwoItemCoins, TwoItemBuy.Select(i => i with { Median = -1 }).ToArray(),
            (tag, _) => tag == "TITANIUM_DRILL_3" ? 200_000_000 : 60_000_000);

        var flip = (await TradeFlips(trade, Sale(trade, 0, 249_999_000)))["TITANIUM_DRILL_3"];

        Assert.That(flip.cost, Is.EqualTo(226_923_076));
        Assert.That((int)flip.flags & (4 | 16), Is.EqualTo(4));
        Assert.That(trade.TagRequests.Select(r => r.tag), Is.EqualTo(new[] { "TITANIUM_DRILL_3", "PET_GLACITE_GOLEM" }));
    }

    /// <summary>A pet stored without petInfo takes its level from the [Lvl N] of its name.</summary>
    [TestCase(100)]
    [TestCase(57)]
    public async Task PetLevelFallsBackToItemName(int level)
    {
        var pet = new TradeItem("PET_BAL", $"§7[Lvl {level}] §6Bal", """{"tier": 5}""", level == 100 ? 60_000_000 : 0);
        var trade = CreateTrade(TwoItemCoins, [TwoItemBuy[0], pet],
            (tag, filters) => filters.GetValueOrDefault("PetLevel") == "57" && filters.GetValueOrDefault("Rarity") == "LEGENDARY" ? 60_000_000 : 0);

        var flip = (await TradeFlips(trade, Sale(trade, 0, 249_999_000)))["TITANIUM_DRILL_3"];

        Assert.That(flip.cost, Is.EqualTo(226_923_076));
        Assert.That((int)flip.flags & (4 | 16), Is.EqualTo(4));
        var seenBySniper = trade.SniperRequests.Single().SingleOrDefault(a => a.Tag == "PET_BAL");
        if (level == 100)
            Assert.That(seenBySniper.FlatenedNBT["exp"], Is.EqualTo("25353230"), "a maxed pet is priced as one");
        else
            Assert.That(seenBySniper, Is.Null, "without exp the sniper can not see the level");
        Assert.That(trade.TagRequests.Count, Is.EqualTo(level == 100 ? 0 : 1));
    }

    /// <summary>An item swapped for another one plus coins back keeps the single-item cost: 250M paid minus 50M returned.</summary>
    [Test]
    public async Task SwapWithCoinsBackKeepsSingleItemCost()
    {
        var first = State(4000001, new("SATIN_TROUSERS", "Satin Trousers", """{"color": "15:11:10", "tier": 3}""", 0));
        var second = State(4000002, new("SATIN_TROUSERS", "Satin Trousers", """{"color": "4:8:11", "tier": 3}""", 0));
        var rows = new List<Transaction>
        {
            Entry(first.Id.Value, 33, 1, Bought.AddDays(-2)), Entry(Coins, 34, 2_500_000_000, Bought.AddDays(-2)),
            Entry(first.Id.Value, 34, 1, Bought), Entry(second.Id.Value, 33, 1, Bought), Entry(Coins, 33, 500_000_000, Bought),
            Entry(second.Id.Value, 34, 1, Sold), Entry(Coins, 33, 2_800_000_000, Sold)
        };
        var trade = CreateTrade([first, second], rows, [0, 0], (_, _) => 0);

        var flip = (await TradeFlips(trade, Sale(trade, 1, 280_000_000))).Single().Value;

        Assert.That(flip.cost, Is.EqualTo(200_000_000));
        Assert.That((int)flip.flags & (4 | 8 | 16), Is.Zero);
        Assert.That(flip.change.Label, Is.EqualTo("Item was bought by trade for 200000000 coins"));
        Assert.That(trade.SniperRequests, Is.Empty);
    }

    private sealed record TradeItem(string Tag, string Name, string Attributes, long Median, string Enchantments = "{}");

    // The resolved trades as stored, item data only: identifiers and the two fields the diagnostics could not
    // return (gems.unlocked_slots, petInfo.extraData) are left out. The four sale trades resolved to exactly the
    // items of BundleSells. Medians are test inputs for the sniper, 0 where it has none, negative where it fails.
    private const long BundleCoins = 2_900_000_000;
    private static readonly TradeItem[] BundleBuy =
    [
        new("PET_BAL", "§7[Lvl 100] §6Bal§d ✦", """{"petInfo": {"active": false, "candyUsed": 0, "exp": 27923968.73310481, "heldItem": "PET_ITEM_MINING_SKILL_BOOST_RARE", "hideInfo": false, "hideRightClick": false, "noMove": false, "petSoulbound": false, "skin": "BAL_BLIZZARD", "tier": "LEGENDARY", "type": "BAL"}, "tier": 5}""", 0),
        new("DIVAN_DRILL", "Lustrous Divan's Drill", """{"compact_blocks": 378035, "divan_powder_coating": 1, "drill_fuel": 91324, "drill_part_engine": "amber_polished_drill_engine", "drill_part_fuel_tank": "perfectly_cut_fuel_tank", "drill_part_upgrade_module": "goblin_omelette_sunny_side", "engine": {"id": "AMBER_POLISHED_DRILL_ENGINE"}, "gems": {"AMBER_0": "PERFECT", "AMBER_1": "PERFECT", "JADE_0": "PERFECT", "JADE_1": "PERFECT", "MINING_0": "PERFECT", "MINING_0_gem": "TOPAZ"}, "modifier": "lustrous", "polarvoid": 5, "power_ability_scroll": "AMBER_POWER_SCROLL", "rarity_upgrades": 1, "tier": 9, "upgrade_module": {"id": "GOBLIN_OMELETTE_SUNNY_SIDE"}}""", 1_400_000_000, """{"compact": 8, "efficiency": 10, "experience": 4, "fortune": 4, "lapidary": 5, "paleontologist": 5, "pristine": 5, "smelting_touch": 1, "ultimate_flowstate": 3}"""),
        new("SAPPHIRE_CLOAK", "Blazing Sapphire Cloak", """{"modifier": "blazing", "tier": 4}""", 60_000_000),
        new("DWARVEN_HANDWARMERS", "Blazing Dwarven Handwarmers", """{"modifier": "blazing", "tier": 4}""", 90_000_000),
        new("TITANIUM_BELT", "Blazing Titanium Belt", """{"modifier": "blazing", "rarity_upgrades": 1, "tier": 4}""", 0),
        new("GEMSTONE_DRILL_4", "Refined Jasper Drill X", """{"drill_fuel": 0, "drill_part_upgrade_module": "goblin_omelette_spicy", "modifier": "refined", "tier": 4, "upgrade_module": {"id": "GOBLIN_OMELETTE_SPICY", "stored_drill_fuel": 99953}}""", 30_000_000, """{"efficiency": 5, "experience": 3, "fortune": 3, "ultimate_wise": 1}"""),
        new("PET_ARMADILLO", "§7[Lvl 100] §6Armadillo", """{"petInfo": {"active": false, "candyUsed": 0, "exp": 25402358.367306218, "heldItem": "PET_ITEM_MINING_SKILL_BOOST_RARE", "hideInfo": false, "hideRightClick": false, "noMove": false, "petSoulbound": false, "tier": "LEGENDARY", "type": "ARMADILLO"}, "tier": 5}""", 0),
        new("GLOSSY_MINERAL_LEGGINGS", "Jaded Glossy Mineral Leggings", """{"modifier": "jaded", "rarity_upgrades": 1, "tier": 8}""", 35_000_000),
        new("GLOSSY_MINERAL_BOOTS", "Jaded Glossy Mineral Boots", """{"modifier": "jaded", "rarity_upgrades": 1, "tier": 8}""", 35_000_000),
        new("GLOSSY_MINERAL_CHESTPLATE", "Jaded Glossy Mineral Chestplate", """{"modifier": "jaded", "rarity_upgrades": 1, "tier": 8}""", 35_000_000),
        new("GLOSSY_MINERAL_HELMET", "Loadout 8", """{"modifier": "jaded", "rarity_upgrades": 1}""", 35_000_000),
        new("DIVAN_HELMET", "Jaded Helmet of Divan", """{"gems": {"AMBER_0": "ROUGH", "AMBER_1": "ROUGH", "JADE_0": "ROUGH", "JADE_1": "ROUGH", "TOPAZ_0": "ROUGH"}, "modifier": "jaded", "rarity_upgrades": 1, "tier": 8}""", 150_000_000, """{"ice_cold": 1, "ultimate_wisdom": 5}"""),
        new("DIVAN_CHESTPLATE", "Jaded Chestplate of Divan", """{"gems": {"AMBER_0": "ROUGH", "AMBER_1": "ROUGH", "JADE_0": "ROUGH", "JADE_1": "ROUGH", "TOPAZ_0": "ROUGH"}, "modifier": "jaded", "rarity_upgrades": 1, "tier": 8}""", 150_000_000, """{"ultimate_wisdom": 5}"""),
        new("DIVAN_LEGGINGS", "Jaded Leggings of Divan", """{"gems": {"AMBER_0": "ROUGH", "AMBER_1": "ROUGH", "JADE_0": "ROUGH", "JADE_1": "ROUGH", "TOPAZ_0": "ROUGH"}, "modifier": "jaded", "rarity_upgrades": 1, "tier": 8}""", 150_000_000, """{"ultimate_wisdom": 5}"""),
        new("DIVAN_BOOTS", "Jaded Boots of Divan", """{"gems": {"AMBER_0": "ROUGH", "AMBER_1": "ROUGH", "JADE_0": "ROUGH", "JADE_1": "ROUGH", "TOPAZ_0": "ROUGH"}, "modifier": "jaded", "rarity_upgrades": 1, "tier": 8}""", 150_000_000, """{"ultimate_wisdom": 5}"""),
        new("DIVAN_PENDANT", "Blazing Pendant of Divan", """{"gems": {"AMBER_0": "ROUGH", "JADE_0": "ROUGH"}, "modifier": "blazing", "rarity_upgrades": 1, "tier": 8}""", 450_000_000),
    ];
    private static readonly (long coins, string[] tags)[] BundleSells =
    [
        (175_000_000, ["GLOSSY_MINERAL_LEGGINGS", "GLOSSY_MINERAL_BOOTS", "GLOSSY_MINERAL_CHESTPLATE", "GLOSSY_MINERAL_HELMET"]),
        (615_000_000, ["DWARVEN_HANDWARMERS", "DIVAN_PENDANT"]),
        (2_400_000_000, ["DIVAN_DRILL", "DIVAN_HELMET", "DIVAN_CHESTPLATE", "DIVAN_LEGGINGS", "DIVAN_BOOTS"]),
        (75_000_000, ["SAPPHIRE_CLOAK"]),
    ];
    private const long TwoItemCoins = 295_000_000;
    private static readonly TradeItem[] TwoItemBuy =
    [
        new("TITANIUM_DRILL_3", "Stellar Titanium Drill DR-X555", """{"compact_blocks": 196368, "divan_powder_coating": 1, "drill_fuel": 735, "drill_part_engine": "titanium_drill_engine", "drill_part_fuel_tank": "titanium_fuel_tank", "drill_part_upgrade_module": "goblin_omelette_spicy", "engine": {"id": "TITANIUM_DRILL_ENGINE"}, "fuel_tank": {"id": "TITANIUM_FUEL_TANK"}, "gems": {"AMBER_0": "FLAWLESS", "JADE_0": "FLAWLESS"}, "modifier": "stellar", "polarvoid": 5, "rarity_upgrades": 1, "tier": 5, "upgrade_module": {"id": "GOBLIN_OMELETTE_SPICY"}}""", 200_000_000, """{"compact": 8, "efficiency": 10, "experience": 4, "fortune": 4, "paleontologist": 5, "ultimate_flowstate": 3}"""),
        new("PET_GLACITE_GOLEM", "§7[Lvl 100] §5Glacite Golem", """{"petInfo": {"active": true, "candyUsed": 0, "exp": 392338765.75743145, "heldItem": "BEJEWELED_COLLAR", "hideInfo": false, "hideRightClick": false, "noMove": false, "petSoulbound": false, "tier": "EPIC", "type": "GLACITE_GOLEM"}}""", 60_000_000),
    ];

    private sealed record Trade(List<PlayerState.Client.Model.Item> States, Mock<PlayerState.Client.Api.IItemsApi> Items,
        Mock<PlayerState.Client.Api.ITransactionApi> Api, RepresentationConverter Converter,
        List<List<SaveAuction>> SniperRequests, List<(string tag, Dictionary<string, string> filters)> TagRequests);

    /// <summary>The bundle purchase; the named tags have no estimate at all, like the Armadillo.</summary>
    private static Trade CreateBundle(params string[] unpriced)
    {
        var items = BundleBuy.Select(i => unpriced.Contains(i.Tag) ? i with { Median = 0 } : i).ToArray();
        return CreateTrade(BundleCoins, items, (tag, filters) => tag switch
        {
            "PET_BAL" when filters.Count == 2 => 60_000_000,
            "TITANIUM_BELT" when !filters.ContainsKey("Reforge") => 20_000_000,
            _ => 0
        });
    }

    /// <summary>A trade giving coins for the items, received at the class purchase time.</summary>
    private static Trade CreateTrade(long coins, TradeItem[] items, Func<string, Dictionary<string, string>, long> tagPrice)
    {
        var states = items.Select((item, i) => State(3000000 + i, item)).ToList();
        var rows = states.Select(s => Entry(s.Id.Value, 33, 1, Bought)).Append(Entry(Coins, 34, coins * 10, Bought)).ToList();
        return CreateTrade(states, rows, items.Select(i => i.Median).ToArray(), tagPrice);
    }

    private static Trade CreateTrade(List<PlayerState.Client.Model.Item> states, List<Transaction> rows, long[] medians,
        Func<string, Dictionary<string, string>, long> tagPrice)
    {
        // every read returns its own copy, converting an item state consumes its reforge
        var items = new Mock<PlayerState.Client.Api.IItemsApi>();
        items.Setup(x => x.ApiItemsFindUuidPostAsync(It.IsAny<List<PlayerState.Client.Model.ItemIdSearch>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<PlayerState.Client.Model.ItemIdSearch> search, int index, CancellationToken token) =>
                states.Where(s => Guid.Parse(s.ExtraAttributes["uuid"].ToString()) == search[0].Uuid).Select(Copy).ToList());
        items.Setup(x => x.ApiItemsIdGetAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id, int index, CancellationToken token) => Copy(states.Single(s => s.Id == id)));
        var sniperRequests = new List<List<SaveAuction>>();
        var sniper = new Mock<ISniperClient>();
        sniper.Setup(s => s.GetPrices(It.IsAny<IEnumerable<SaveAuction>>()))
            .ReturnsAsync((IEnumerable<SaveAuction> auctions) =>
            {
                sniperRequests.Add(auctions.ToList());
                if (medians.Any(m => m < 0))
                    throw new JsonReaderException("sniper answered with an error page");
                return auctions.Select(a => medians[states.FindIndex(s => s.ExtraAttributes["uuid"].ToString() == a.FlatenedNBT["uuid"])])
                    .Select(median => median > 0 ? new Sniper.Client.Model.PriceEstimate { Median = median } : null).ToList();
            });
        var tagRequests = new List<(string, Dictionary<string, string>)>();
        var prices = new Mock<Api.Client.Api.IPricesApi>();
        prices.Setup(p => p.ApiItemPriceItemTagGetAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tag, Dictionary<string, string> filters, int index, CancellationToken token) =>
            {
                tagRequests.Add((tag, filters));
                return tagPrice(tag, filters) is > 0 and var median ? new() { Median = median } : null;
            });
        // the tag-level price source is a constructor argument the base does not have
        var constructor = typeof(RepresentationConverter).GetConstructors().Single();
        var converter = (RepresentationConverter)constructor.Invoke(new object[] { NullLogger<RepresentationConverter>.Instance, sniper.Object, prices.Object }
            .Take(constructor.GetParameters().Length).ToArray());
        return new(states, items, Transactions(rows), converter, sniperRequests, tagRequests);
    }

    private static PlayerState.Client.Model.Item State(int id, TradeItem item)
    {
        var state = JObject.FromObject(new { id, tag = item.Tag, itemName = item.Name, count = 1, enchantments = JObject.Parse(item.Enchantments) })
            .ToObject<PlayerState.Client.Model.Item>();
        state.ExtraAttributes = JsonConvert.DeserializeObject<Dictionary<string, object>>(item.Attributes);
        // synthetic identifiers, the stored ones are not part of the fixture
        state.ExtraAttributes["uuid"] = $"00000000-0000-0000-0000-{id:x12}";
        state.ExtraAttributes["uid"] = $"{id:x12}";
        return state;
    }

    private static PlayerState.Client.Model.Item Copy(PlayerState.Client.Model.Item state)
    {
        return JsonConvert.DeserializeObject<PlayerState.Client.Model.Item>(JsonConvert.SerializeObject(state));
    }

    /// <summary>The item leaving in a later trade for the given coins.</summary>
    private static SaveAuction Sale(Trade trade, int index, long price)
    {
        var sell = trade.Converter.FromItemRepresent(Copy(trade.States[index]));
        sell.AuctioneerId = Player.ToString("N");
        sell.Uuid = Guid.Empty.ToString("N");
        sell.UId = trade.States[index].Id.Value;
        sell.End = Sold;
        sell.HighestBidAmount = price;
        return sell;
    }

    /// <summary>One sale trade of the bundle, its coins split by median share the way the sale side stores them.</summary>
    private static List<SaveAuction> BatchSales(Trade bundle, (long coins, string[] tags) batch)
    {
        var medians = batch.tags.Select(tag => BundleBuy.Single(i => i.Tag == tag).Median).ToList();
        return batch.tags.Select((tag, i) => Sale(bundle, Array.FindIndex(BundleBuy, b => b.Tag == tag), batch.coins * medians[i] / medians.Sum())).ToList();
    }

    /// <summary>Runs the given sales, or one of every received item, through the linked-auction trade check.</summary>
    private static async Task<Dictionary<string, (long cost, FlipFlags flags, PastFlip.ProfitChange change)>> TradeFlips(Trade trade, params SaveAuction[] sells)
    {
        var tracker = new TrackerService(null, NullLogger<TrackerService>.Instance, null, null, null, null,
            null, new ActivitySource("bundle-trade"), null, null, null, trade.Items.Object, trade.Api.Object, trade.Converter);
        var method = typeof(TrackerService).GetMethod("CheckTrade", BindingFlags.Instance | BindingFlags.NonPublic);
        var flips = new Dictionary<string, (long, FlipFlags, PastFlip.ProfitChange)>();
        foreach (var sell in sells.Length > 0 ? sells : trade.States.Select((_, i) => Sale(trade, i, 0)).ToArray())
        {
            var buy = new ApiSaveAuction { Bids = new() { new() { Bidder = "prior-owner" } }, FlatenedNBT = new(), End = Bought.AddDays(-10) };
            var result = await (Task<(FlipFlags flags, PastFlip.ProfitChange change)>)method.Invoke(tracker, new object[] { buy, sell });
            flips.Add(sell.Tag, (buy.HighestBidAmount, result.flags, result.change));
        }
        return flips;
    }

    /// <summary>Sells the items in one batch, each item's only known source being the trade.</summary>
    private static async Task<List<PastFlip>> IndexSales(Trade trade, List<SaveAuction> sells, FlipSumaryEventProducer producer)
    {
        trade.Api.Setup(x => x.TransactionUuidItemIdPostAsync(It.IsAny<List<Guid>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(trade.States.Where(s => sells.Any(sell => sell.UId == s.Id)).ToDictionary(s => s.ExtraAttributes["uuid"].ToString(), s => new List<long> { s.Id.Value }));
        var auctions = new Mock<Api.Client.Api.IAuctionsApi>();
        auctions.Setup(x => x.ApiAuctionsUidsSoldPostWithHttpInfoAsync(It.IsAny<Api.Client.Model.InventoryBatchLookup>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Api.Client.Client.ApiResponse<Dictionary<string, List<Api.Client.Model.ItemSell>>>(
                System.Net.HttpStatusCode.OK, null, new Dictionary<string, List<Api.Client.Model.ItemSell>>()));
        var saved = new System.Collections.Concurrent.ConcurrentBag<PastFlip>();
        var storage = new Mock<FlipStorageService>(null, null, null);
        storage.Setup(x => x.SaveFlip(It.IsAny<PastFlip>())).Callback<PastFlip>(saved.Add).Returns(Task.CompletedTask);
        var changes = new Mock<ProfitChangeService>(null, null, null, null, null, null, null, null, null);
        changes.Setup(x => x.GetChanges(It.IsAny<SaveAuction>(), It.IsAny<SaveAuction>()))
            .Returns(() => Task.FromResult(new List<PastFlip.ProfitChange>()));
        var tracker = new TrackerService(null, NullLogger<TrackerService>.Instance, auctions.Object,
            producer, null, changes.Object, storage.Object, new ActivitySource("bundle-sale"),
            null, null, new Mock<Settings.Client.Api.ISettingsApi>().Object, trade.Items.Object, trade.Api.Object, trade.Converter);

        await tracker.IndexCassandra(sells);

        return saved.ToList();
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
