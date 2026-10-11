using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Models.Investigation;
using FluentAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class PurchaseReportBuilderTests
{
    private static PurchaseFunnelEntry Entry(FunnelVerdict verdict, long profit, double? lead = null, double? sendLead = null, string finder = null) => new()
    {
        Verdict = verdict,
        Profit = profit,
        Found = finder != null,
        LeadSeconds = lead,
        SendLeadSeconds = sendLead,
        Finders = finder == null ? [] : [finder]
    };

    [Test]
    public void EmptyReportHasZeros()
    {
        var report = PurchaseReportBuilder.Build([]);
        report.Count.Should().Be(0);
        report.FoundPct.Should().Be(0);
        report.MedianLeadSeconds.Should().BeNull();
    }

    [Test]
    public void ComputesPercentagesMediansAndProfits()
    {
        var report = PurchaseReportBuilder.Build(
        [
            Entry(FunnelVerdict.BoughtByUser, 100, 10, 4, "SNIPER"),
            Entry(FunnelVerdict.FoundAfterSold, 50, -2, null, "FLIPPER"),
            Entry(FunnelVerdict.NotFound, 30),
            Entry(FunnelVerdict.FoundNotSent, 20, 6, null, "SNIPER"),
        ]);
        report.Count.Should().Be(4);
        report.FoundPct.Should().Be(75);
        report.FoundBeforePurchasePct.Should().Be(50);
        report.MedianLeadSeconds.Should().Be(6); // -2, 6, 10 odd count
        report.MedianSendLeadSeconds.Should().Be(4);
        report.TotalProfit.Should().Be(200);
        report.ProfitNotFound.Should().Be(30);
        report.ProfitFoundNotReceived.Should().Be(20);
        report.ByFinder.Should().BeEquivalentTo(new Dictionary<string, int> { ["SNIPER"] = 2, ["FLIPPER"] = 1 });
    }

    [Test]
    public void MedianOfEvenCountAveragesMiddle()
    {
        var report = PurchaseReportBuilder.Build([Entry(FunnelVerdict.BoughtByUser, 1, 2), Entry(FunnelVerdict.BoughtByUser, 1, 4)]);
        report.MedianLeadSeconds.Should().Be(3);
    }

    [Test]
    public void ByVerdictUsesVerdictNames()
    {
        var report = PurchaseReportBuilder.Build([Entry(FunnelVerdict.NotFound, 1), Entry(FunnelVerdict.NotFound, 1), Entry(FunnelVerdict.SentNotClicked, 1)]);
        report.ByVerdict.Should().BeEquivalentTo(new Dictionary<string, int> { ["NotFound"] = 2, ["SentNotClicked"] = 1 });
    }

    [Test]
    public void TopReceiversAreMergedAcrossChunksExcludingOwnAndSharesComputed()
    {
        var chunks = new List<List<KeyValuePair<long, int>>>
        {
            new() { new(1, 5), new(2, 3), new(9, 40) },
            new() { new(1, 4), new(3, 6), new(2, 1) },
        };
        var top = PurchaseReportBuilder.MergeTopReceivers(chunks, new HashSet<long> { 9 }, 20);
        top.Select(t => t.Hash).Should().Equal(1, 3, 2);
        top[0].ReceivedCount.Should().Be(9);
        top[0].SharePct.Should().Be(45);
        top[1].SharePct.Should().Be(30);
    }

    [Test]
    public void TopReceiversAreCappedAtTen()
    {
        var chunk = Enumerable.Range(1, 25).Select(i => new KeyValuePair<long, int>(i, i)).ToList();
        var top = PurchaseReportBuilder.MergeTopReceivers([chunk], new HashSet<long>(), 100);
        top.Should().HaveCount(10);
        top[0].Hash.Should().Be(25);
    }

    [Test]
    public void TopReceiversShareIsZeroWithoutReceives()
        => PurchaseReportBuilder.MergeTopReceivers([[new KeyValuePair<long, int>(1, 1)]], new HashSet<long>(), 0)[0].SharePct.Should().Be(0);

    [Test]
    public void TradesAreExcludedFromPercentagesAndVerdicts()
    {
        var trade = Entry(FunnelVerdict.Unknown, 500);
        trade.ViaTrade = true;
        var other = Entry(FunnelVerdict.NotFound, 30);
        other.BoughtByOtherAccount = true;
        var report = PurchaseReportBuilder.Build([Entry(FunnelVerdict.BoughtByUser, 100, 10, 4, "SNIPER"), other, trade]);
        report.Count.Should().Be(3);
        report.AuctionPurchaseCount.Should().Be(2);
        report.TradeCount.Should().Be(1);
        report.OtherBuyerCount.Should().Be(1);
        report.ProfitViaTrade.Should().Be(500);
        report.TotalProfit.Should().Be(630);
        report.FoundPct.Should().Be(50);
        report.ByVerdict.Should().BeEquivalentTo(new Dictionary<string, int> { ["BoughtByUser"] = 1, ["NotFound"] = 1 });
        report.ProfitNotFound.Should().Be(30);
    }

    [Test]
    public void OnlyTradesGiveEmptySummary()
    {
        var trade = Entry(FunnelVerdict.Unknown, 5);
        trade.ViaTrade = true;
        var report = PurchaseReportBuilder.Build([trade]);
        report.TradeCount.Should().Be(1);
        report.FoundPct.Should().Be(0);
        report.ByVerdict.Should().BeEmpty();
    }

    private static readonly DateTime Sold = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PurchaseFunnelEntry Purchase(params string[] flags) => new()
    {
        PurchaseTime = Sold, Profit = 10, Flags = flags.ToList(),
        ViaTrade = flags.Contains("ViaTrade") || flags.Contains("MultiItemTrade")
    };

    private static List<EventAggregate> Aggs(params (FlipEventType type, double sec)[] e)
        => e.Select(x => new EventAggregate { Type = x.type, Count = 1, Min = Sold.AddSeconds(x.sec), Max = Sold.AddSeconds(x.sec) }).ToList();

    private static FlipEvent Ev(FlipEventType type, long player, double sec) => new() { Type = type, PlayerId = player, Timestamp = Sold.AddSeconds(sec) };

    private static readonly FinderContext Found = new() { Finder = LowPricedAuction.FinderType.SNIPER, FoundTime = Sold.AddSeconds(-5) };

    [Test]
    public void TradeEntryIsNotClassified()
    {
        var entry = Purchase("ViaTrade");
        PurchaseReportBuilder.ClassifyEntry(entry, [], [], null, new HashSet<long> { 1 }, [], []);
        entry.Verdict.Should().Be(FunnelVerdict.Unknown);
        entry.Explanation.Should().Be("acquired via trade, no auction funnel");
    }

    [Test]
    public void ActualBuyerAccountIsUsedForClassification()
    {
        var entry = Purchase("DifferentBuyer");
        var aggs = Aggs((FlipEventType.AUCTION_SOLD, 0), (FlipEventType.FLIP_RECEIVE, -2));
        // player 1 is the seller of the flip, account 9 bought and received it from us
        var events = new[] { Ev(FlipEventType.FLIP_RECEIVE, 9, -2), Ev(FlipEventType.AUCTION_SOLD, 9, 0) };
        PurchaseReportBuilder.ClassifyEntry(entry, aggs, events, 9, new HashSet<long> { 1 }, [], [Found]);
        entry.BoughtByOtherAccount.Should().BeTrue();
        entry.SoldToHash.Should().Be(9);
        entry.BuyerReceived.Should().BeTrue();
        entry.Verdict.Should().Be(FunnelVerdict.BoughtByUser);
    }

    [Test]
    public void ReceiptByUnrelatedAccountDoesNotCountAsBuyer()
    {
        var entry = Purchase();
        var aggs = Aggs((FlipEventType.AUCTION_SOLD, 0), (FlipEventType.FLIP_RECEIVE, -2));
        var events = new[] { Ev(FlipEventType.FLIP_RECEIVE, 77, -2) };
        PurchaseReportBuilder.ClassifyEntry(entry, aggs, events, 9, new HashSet<long> { 1 }, [], [Found]);
        entry.BuyerReceived.Should().BeFalse();
        entry.BoughtByOtherAccount.Should().BeTrue();
        entry.Verdict.Should().Be(FunnelVerdict.SentNotClicked);
    }

    [Test]
    public void OwnPurchaseIsNotMarkedAsOtherBuyer()
    {
        var entry = Purchase();
        PurchaseReportBuilder.ClassifyEntry(entry, Aggs((FlipEventType.AUCTION_SOLD, 0)), [], 1, new HashSet<long> { 1 }, [], [Found]);
        entry.BoughtByOtherAccount.Should().BeFalse();
        entry.Verdict.Should().Be(FunnelVerdict.FoundNotSent);
    }
}
