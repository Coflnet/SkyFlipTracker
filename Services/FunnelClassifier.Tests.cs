using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Models.Investigation;
using FluentAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class FunnelClassifierTests
{
    private static readonly DateTime Sold = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const long Buyer = 111;
    private const long Other = 222;
    private static readonly HashSet<long> BuyerSet = new() { Buyer };

    private static FlipEvent Ev(FlipEventType type, long player, double secondsFromSold) =>
        new() { Type = type, PlayerId = player, Timestamp = Sold.AddSeconds(secondsFromSold) };

    private static FinderContext Context(double secondsFromSold) =>
        new() { Finder = LowPricedAuction.FinderType.SNIPER, FoundTime = Sold.AddSeconds(secondsFromSold) };

    private static FunnelInput Input(double? foundFromSold = -5, long profit = 1_000_000, bool? auctionSold = true) => new()
    {
        AuctionSold = auctionSold,
        Profit = profit,
        Contexts = foundFromSold == null ? [] : [Context(foundFromSold.Value)]
    };

    private static FlipEvent SoldEvent() => Ev(FlipEventType.AUCTION_SOLD, Buyer, 0);

    [Test]
    public void NotSoldYet()
        => FunnelClassifier.Classify(Input(auctionSold: false), [], BuyerSet).Verdict.Should().Be(FunnelVerdict.NotSoldYet);

    [Test]
    public void UnknownWithoutSaleInformation()
        => FunnelClassifier.Classify(Input(auctionSold: null), [], BuyerSet).Verdict.Should().Be(FunnelVerdict.Unknown);

    [Test]
    public void NotFound()
        => FunnelClassifier.Classify(Input(null), [SoldEvent()], BuyerSet).Verdict.Should().Be(FunnelVerdict.NotFound);

    [Test]
    public void FoundAfterSold()
        => FunnelClassifier.Classify(Input(2), [SoldEvent()], BuyerSet).Verdict.Should().Be(FunnelVerdict.FoundAfterSold);

    [Test]
    public void FoundNotSent()
        => FunnelClassifier.Classify(Input(), [SoldEvent()], BuyerSet).Verdict.Should().Be(FunnelVerdict.FoundNotSent);

    [Test]
    public void SentAfterSold()
    {
        var result = FunnelClassifier.Classify(Input(), [SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Other, 3.5)], BuyerSet);
        result.Verdict.Should().Be(FunnelVerdict.SentAfterSold);
        result.Timings.FirstReceiveToSoldSeconds.Should().Be(-3.5);
    }

    [Test]
    public void SentNotClicked()
        => FunnelClassifier.Classify(Input(), [SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Other, -2)], BuyerSet)
            .Verdict.Should().Be(FunnelVerdict.SentNotClicked);

    [Test]
    public void ClickedNotBought()
        => FunnelClassifier.Classify(Input(), [SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Other, -2), Ev(FlipEventType.FLIP_CLICK, Other, -1.5)], BuyerSet)
            .Verdict.Should().Be(FunnelVerdict.ClickedNotBought);

    [Test]
    public void BoughtByUser()
    {
        var result = FunnelClassifier.Classify(Input(), [SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Buyer, -2), Ev(FlipEventType.FLIP_CLICK, Buyer, -1.5)], BuyerSet);
        result.Verdict.Should().Be(FunnelVerdict.BoughtByUser);
        result.BuyerReceived.Should().BeTrue();
        result.BuyerClicked.Should().BeTrue();
        result.BuyerSendLeadSeconds.Should().Be(2);
    }

    [Test]
    public void LinkedAccountReceivingCountsAsBuyer()
    {
        var linked = new HashSet<long> { Buyer, 333 };
        FunnelClassifier.Classify(Input(), [SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, 333, -2)], linked).Verdict.Should().Be(FunnelVerdict.BoughtByUser);
        FunnelClassifier.Classify(Input(), [SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, 333, -2)], BuyerSet).Verdict.Should().NotBe(FunnelVerdict.BoughtByUser);
    }

    [Test]
    public void FlipTimestampMinusThreeSecondsIsTheFallback()
    {
        var input = new FunnelInput
        {
            AuctionSold = true,
            Flips = [new Flip { FinderType = LowPricedAuction.FinderType.SNIPER, Timestamp = Sold.AddSeconds(2), TargetPrice = 5 }]
        };
        var result = FunnelClassifier.Classify(input, [SoldEvent()], BuyerSet);
        result.FirstFound.Should().Be(Sold.AddSeconds(-1));
        result.Findings.Single().TimeSource.Should().Be("flip");
        result.Verdict.Should().Be(FunnelVerdict.FoundNotSent);
    }

    [Test]
    public void FinderContextIsPreferredOverFlipTimestamp()
    {
        var input = new FunnelInput
        {
            AuctionSold = true,
            Contexts = [Context(-6)],
            Flips = [new Flip { FinderType = LowPricedAuction.FinderType.SNIPER, Timestamp = Sold.AddSeconds(-1) }]
        };
        var result = FunnelClassifier.Classify(input, [SoldEvent()], BuyerSet);
        result.Findings.Should().ContainSingle().Which.TimeSource.Should().Be("finder_context");
        result.FirstFound.Should().Be(Sold.AddSeconds(-6));
    }

    /// <summary>
    /// The exact predicate the tracker used before the classifier existed
    /// </summary>
    private static bool OldPredicate(bool sentToPurchaser, TimeSpan? diff, long profit)
        => !sentToPurchaser && diff > TimeSpan.FromSeconds(-4) && (diff < TimeSpan.FromSeconds(-3.2) || profit > 50_000_000 && diff < TimeSpan.FromSeconds(-0.1));

    [TestCase(-3.2, 1_000_000)]
    [TestCase(-3.21, 1_000_000)]
    [TestCase(-3.19, 1_000_000)]
    [TestCase(-4.0, 1_000_000)]
    [TestCase(-3.99, 1_000_000)]
    [TestCase(-4.01, 1_000_000)]
    [TestCase(-0.1, 60_000_000)]
    [TestCase(-0.11, 60_000_000)]
    [TestCase(-0.5, 50_000_000)]
    [TestCase(-0.5, 50_000_001)]
    [TestCase(-0.5, 1_000_000)]
    [TestCase(0.5, 60_000_000)]
    public void ExemptPredicateMatchesPreviousBehavior(double receiveSecondsAfterSold, long profit)
    {
        var events = new List<FlipEvent> { SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Other, receiveSecondsAfterSold) };
        var result = FunnelClassifier.Classify(Input(profit: profit), events, BuyerSet);
        var diff = events[0].Timestamp - events[1].Timestamp;
        result.QualifiesForExempt.Should().Be(OldPredicate(false, diff, profit));
    }

    [Test]
    public void BuyerReceivingNeverQualifiesForExempt()
    {
        var events = new List<FlipEvent> { SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Buyer, 3.5) };
        FunnelClassifier.Classify(Input(), events, BuyerSet).QualifiesForExempt.Should().BeFalse();
    }

    [Test]
    public void ExemptWithoutSoldEventIsFalse()
        => FunnelClassifier.Classify(Input(), [Ev(FlipEventType.FLIP_RECEIVE, Other, 3.5), Ev(FlipEventType.FLIP_CLICK, Other, 4)], BuyerSet)
            .QualifiesForExempt.Should().BeFalse();

    [Test]
    public void AggregateOverloadAgreesWithRawOverload()
    {
        var scenarios = new List<List<FlipEvent>>
        {
            new() { SoldEvent() },
            new() { SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Other, 3.5) },
            new() { SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Other, -2), Ev(FlipEventType.FLIP_RECEIVE, Other, -1) },
            new() { SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Other, -2), Ev(FlipEventType.FLIP_CLICK, Other, -1) },
            new() { SoldEvent(), Ev(FlipEventType.FLIP_RECEIVE, Buyer, -2), Ev(FlipEventType.FLIP_RECEIVE, Other, -3), Ev(FlipEventType.FLIP_CLICK, Buyer, -1) },
        };
        foreach (var events in scenarios)
        {
            var raw = FunnelClassifier.Classify(Input(), events, BuyerSet);
            var aggregates = events.GroupBy(e => e.Type).Select(g => new EventAggregate
            {
                AuctionId = 1, Type = g.Key, Count = g.Count(), Min = g.Min(e => e.Timestamp), Max = g.Max(e => e.Timestamp)
            });
            var aggregated = FunnelClassifier.Classify(Input(), EventStats.FromAggregates(aggregates, events.Where(e => BuyerSet.Contains(e.PlayerId))));
            aggregated.Verdict.Should().Be(raw.Verdict);
            aggregated.QualifiesForExempt.Should().Be(raw.QualifiesForExempt);
            aggregated.BuyerReceived.Should().Be(raw.BuyerReceived);
            aggregated.Timings.FirstReceiveToSoldSeconds.Should().Be(raw.Timings.FirstReceiveToSoldSeconds);
            aggregated.Timings.FoundToSoldSeconds.Should().Be(raw.Timings.FoundToSoldSeconds);
            aggregated.BuyerSendLeadSeconds.Should().Be(raw.BuyerSendLeadSeconds);
        }
    }
}
