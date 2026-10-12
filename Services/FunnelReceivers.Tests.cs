using Coflnet.Sky.Core;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Models.Investigation;
using FluentAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class FunnelReceiversTests
{
    private static readonly DateTime Sold = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<long> NoBuyer = new();

    private static FlipEvent Ev(FlipEventType type, long player, double secondsFromSold) =>
        new() { Type = type, PlayerId = player, Timestamp = Sold.AddSeconds(secondsFromSold) };

    private static FunnelResult Run(params FlipEvent[] events)
        => FunnelClassifier.Classify(new FunnelInput { AuctionSold = true }, events, NoBuyer);

    private static FunnelTimeline Timeline(params FlipEvent[] events)
    {
        var stats = EventStats.FromEvents(events, NoBuyer);
        return new FunnelTimeline { FirstClick = stats.FirstClick, FirstPurchaseStart = stats.FirstPurchaseStart };
    }

    [Test]
    public void ReceiversOrderedAndCapped()
    {
        var events = new List<FlipEvent> { Ev(FlipEventType.AUCTION_SOLD, 1, 0) };
        for (var i = 0; i < 60; i++)
            events.Add(Ev(FlipEventType.FLIP_RECEIVE, 1000 + i, -60 + (59 - i)));
        var result = Run(events.ToArray());
        result.ReceiverCount.Should().Be(60);
        result.Receivers.Should().HaveCount(50);
        result.Receivers.Select(r => r.ReceivedAt).Should().BeInAscendingOrder();
        result.Receivers[0].Hash.Should().Be(1059);
    }

    [Test]
    public void ReceiveThenClickGetsSeconds()
    {
        var result = Run(Ev(FlipEventType.AUCTION_SOLD, 9, 0), Ev(FlipEventType.FLIP_RECEIVE, 5, -10), Ev(FlipEventType.FLIP_CLICK, 5, -8.5));
        var receiver = result.Receivers.Single();
        receiver.ReceiveToClickSeconds.Should().Be(1.5);
        receiver.ReceiveToSoldSeconds.Should().Be(10);
        receiver.ClickedAt.Should().Be(Sold.AddSeconds(-8.5));
    }

    [Test]
    public void ClickWithoutReceiveIsListed()
    {
        var result = Run(Ev(FlipEventType.FLIP_CLICK, 5, -4));
        var receiver = result.Receivers.Single();
        receiver.ReceivedAt.Should().Be(Sold.AddSeconds(-4));
        receiver.ReceiveToClickSeconds.Should().Be(0);
        receiver.ReceiveToSoldSeconds.Should().BeNull();
    }

    [Test]
    public void FirstClickAndPurchaseStartAreEarliest()
    {
        var timeline = Timeline(
            Ev(FlipEventType.FLIP_CLICK, 1, -3), Ev(FlipEventType.FLIP_CLICK, 2, -7),
            Ev(FlipEventType.PURCHASE_START, 1, -2), Ev(FlipEventType.PURCHASE_START, 2, -5));
        timeline.FirstClick.Should().Be(Sold.AddSeconds(-7));
        timeline.FirstPurchaseStart.Should().Be(Sold.AddSeconds(-5));
    }

    [Test]
    public void MedianOverClickedReceivers()
    {
        var result = Run(
            Ev(FlipEventType.FLIP_RECEIVE, 1, -10), Ev(FlipEventType.FLIP_CLICK, 1, -9),
            Ev(FlipEventType.FLIP_RECEIVE, 2, -10), Ev(FlipEventType.FLIP_CLICK, 2, -6),
            Ev(FlipEventType.FLIP_RECEIVE, 3, -10));
        result.Timings.MedianReceiveToClickSeconds.Should().Be(2.5);
        result.Timings.FirstReceiveToFirstClickSeconds.Should().Be(1);
    }
}
