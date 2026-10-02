using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class TrackerBackgroundServiceTests
{
    private static SaveAuction Sell(string tag) => new() { Uuid = Guid.NewGuid().ToString("N"), Tag = tag };

    private static double ErrorCount(string topic) =>
        TrackerBackgroundService.consumeErrors.WithLabels(topic).Value;

    [Test]
    public async Task BadMessageIsSkippedCountedAndOthersProcessed()
    {
        var topic = "test-bad-" + Guid.NewGuid();
        var sells = new[] { Sell("A"), Sell("POISON"), Sell("C") };
        var processed = new List<string>();
        var before = ErrorCount(topic);

        await TrackerBackgroundService.ProcessResilient(sells, async batch =>
        {
            await Task.Yield();
            if (batch.Any(s => s.Tag == "POISON"))
                throw new Exception("could not find kat cost");
            processed.AddRange(batch.Select(s => s.Tag));
        }, topic, NullLogger.Instance, default, (_, _) => Task.CompletedTask);

        processed.Should().BeEquivalentTo(new[] { "A", "C" });
        (ErrorCount(topic) - before).Should().Be(1);
    }

    [Test]
    public async Task InfrastructureErrorIsRetriedNotSkipped()
    {
        var topic = "test-infra-" + Guid.NewGuid();
        var sells = new[] { Sell("A"), Sell("B") };
        var attempts = 0;
        var processed = new List<string>();
        var delays = 0;
        var before = ErrorCount(topic);

        await TrackerBackgroundService.ProcessResilient(sells, batch =>
        {
            if (++attempts < 3)
                throw new TimeoutException("cassandra timeout");
            processed.AddRange(batch.Select(s => s.Tag));
            return Task.CompletedTask;
        }, topic, NullLogger.Instance, default, (_, _) => { delays++; return Task.CompletedTask; });

        attempts.Should().Be(3);
        delays.Should().Be(2);
        processed.Should().BeEquivalentTo(new[] { "A", "B" });
        (ErrorCount(topic) - before).Should().Be(0);
    }

    [Test]
    public async Task InfrastructureErrorStopsRetryingOnShutdown()
    {
        using var cts = new CancellationTokenSource();
        var act = () => TrackerBackgroundService.ProcessResilient(new[] { Sell("A") }, _ =>
        {
            cts.Cancel();
            throw new TimeoutException("down");
        }, "test-cancel-" + Guid.NewGuid(), NullLogger.Instance, cts.Token, (_, _) => Task.CompletedTask);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Test]
    public async Task InfrastructureErrorThatLastsEndsTheConsumerInsteadOfRetryingForever()
    {
        var before = TrackerBackgroundService.MaxInfrastructureRetry;
        TrackerBackgroundService.MaxInfrastructureRetry = TimeSpan.Zero;
        try
        {
            var calls = 0;
            var act = () => TrackerBackgroundService.ProcessResilient(new[] { Sell("A") }, _ =>
            {
                calls++;
                throw new TimeoutException("down");
            }, "test-limit-" + Guid.NewGuid(), NullLogger.Instance, CancellationToken.None, (_, _) => Task.CompletedTask);

            await act.Should().ThrowAsync<TimeoutException>();
            calls.Should().Be(1);
        }
        finally
        {
            TrackerBackgroundService.MaxInfrastructureRetry = before;
        }
    }

    [Test]
    public void ClassifiesErrors()
    {
        TrackerBackgroundService.IsInfrastructureError(new TimeoutException()).Should().BeTrue();
        TrackerBackgroundService.IsInfrastructureError(new global::Cassandra.NoHostAvailableException(new Dictionary<System.Net.IPEndPoint, Exception>())).Should().BeTrue();
        TrackerBackgroundService.IsInfrastructureError(new Exception("wrapped", new TimeoutException())).Should().BeTrue();
        TrackerBackgroundService.IsInfrastructureError(new Exception("could not find kat cost for tier 3(RARE)")).Should().BeFalse();
        TrackerBackgroundService.IsInfrastructureError(new CoflnetException("load error", "This sell caused error")).Should().BeFalse();
    }
}
