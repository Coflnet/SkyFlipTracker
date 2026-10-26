using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Core.Services;
using Coflnet.Sky.Items.Client.Api;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// The pods were OOM killed at 800 MiB by sudden bursts: unbounded librdkafka read-ahead on seven consumers
/// and unbounded parallel downloads of the all-crafts / all-bazaar lists.
/// </summary>
public class MemoryBoundsTests
{
    private static IConfiguration KafkaSettings(params (string, string)[] extra)
    {
        var values = new Dictionary<string, string> { ["KAFKA:BROKERS"] = "localhost:9092" };
        foreach (var (key, value) in extra)
            values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Test]
    public void ConsumerConfigCapsReadAheadMemory()
    {
        var consumerConfig = BoundedKafkaConfig.Create(KafkaSettings(), "sky-fliptracker");

        consumerConfig.GroupId.Should().Be("sky-fliptracker");
        consumerConfig.EnableAutoCommit.Should().BeFalse();
        consumerConfig.PartitionAssignmentStrategy.Should().Be(Confluent.Kafka.PartitionAssignmentStrategy.CooperativeSticky);
        // librdkafka defaults are 65536 kbytes read-ahead and 50 MB per fetch per consumer
        consumerConfig.QueuedMaxMessagesKbytes.Should().Be(BoundedKafkaConfig.DefaultQueuedMaxKbytes).And.BeLessThanOrEqualTo(8192);
        consumerConfig.FetchMaxBytes.Should().BeLessThanOrEqualTo(8 * 1024 * 1024);
        consumerConfig.MaxPartitionFetchBytes.Should().BeLessThanOrEqualTo(1024 * 1024);
        consumerConfig.QueuedMinMessages.Should().BeLessThanOrEqualTo(10_000);
    }

    [Test]
    public void PollIntervalOutlastsTheInfrastructureRetry()
    {
        // librdkafka evicts the member after 300s without a poll, ProcessResilient retries for MaxInfrastructureRetry inside the poll loop
        var created = BoundedKafkaConfig.Create(KafkaSettings(), "sky-fliptracker");
        var bound = BoundedKafkaConfig.Bound(new Confluent.Kafka.ConsumerConfig { GroupId = "sky-fliptracker-sell", SessionTimeoutMs = 10000 }, KafkaSettings());

        foreach (var consumerConfig in new[] { created, bound })
        {
            consumerConfig.MaxPollIntervalMs.Should().BeGreaterThan((int)TrackerBackgroundService.MaxInfrastructureRetry.TotalMilliseconds);
            consumerConfig.MaxPollIntervalMs.Should().BeGreaterThan(consumerConfig.SessionTimeoutMs ?? 0);
        }
    }

    [Test]
    public void BoundKeepsOtherSettingsAndHonoursOverride()
    {
        var original = new Confluent.Kafka.ConsumerConfig { GroupId = "g", SessionTimeoutMs = 65000 };

        var bound = BoundedKafkaConfig.Bound(original, KafkaSettings(("KAFKA_CONSUMER_QUEUE_KBYTES", "1024")));

        bound.GroupId.Should().Be("g");
        bound.SessionTimeoutMs.Should().Be(65000);
        bound.QueuedMaxMessagesKbytes.Should().Be(1024);
    }

    [Test]
    public async Task BulkFetchGateNeverRunsMoreThanTheLimitInParallel()
    {
        var gate = new BulkFetchGate(2);
        var running = 0;
        var peak = 0;

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => gate.Run(async () =>
        {
            var now = Interlocked.Increment(ref running);
            lock (gate)
                peak = Math.Max(peak, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return 1;
        })));

        peak.Should().Be(2);
        gate.Available.Should().Be(2);
    }

    [Test]
    public async Task BulkFetchGateReleasesSlotWhenFetchFails()
    {
        var gate = new BulkFetchGate(1);

        Func<Task> failing = () => gate.Run<int>(() => throw new InvalidOperationException("boom"));
        await failing.Should().ThrowAsync<InvalidOperationException>();

        (await gate.Run(() => Task.FromResult(5))).Should().Be(5);
    }

    private static (ProfitChangeService service, Mock<Bazaar.Client.Api.IBazaarApi> bazaar) CreateService()
    {
        var bazaar = new Mock<Bazaar.Client.Api.IBazaarApi>();
        var factory = new Mock<IPriceProviderFactory>();
        var service = new ProfitChangeService(new Mock<IPricesApi>().Object, new Mock<Crafts.Client.Api.IKatApi>().Object,
            new Mock<Crafts.Client.Api.ICraftsApi>().Object, NullLogger<ProfitChangeService>.Instance, new Mock<IItemsApi>().Object,
            new HypixelItemService(new System.Net.Http.HttpClient(), NullLogger<HypixelItemService>.Instance), bazaar.Object,
            factory.Object, new Mock<IAuctionsApi>().Object);
        return (service, bazaar);
    }

    [Test]
    public async Task BazaarPricesAreDownloadedOnceForConcurrentEnchantLookups()
    {
        var (service, bazaar) = CreateService();
        bazaar.Setup(b => b.GetAllPricesAsync(0, default)).Returns(async () =>
        {
            await Task.Delay(30);
            return new List<Bazaar.Client.Model.ItemPrice>();
        });

        // a sell with many new enchants asks for the list once per enchant
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.GetBazaarPrices()));
        await service.GetBazaarPrices();

        bazaar.Verify(b => b.GetAllPricesAsync(0, default), Times.Once);
    }

    [Test]
    public async Task FailedBazaarDownloadIsNotCached()
    {
        var (service, bazaar) = CreateService();
        var calls = 0;
        bazaar.Setup(b => b.GetAllPricesAsync(0, default)).Returns(() =>
        {
            if (++calls == 1)
                throw new System.Net.Http.HttpRequestException("down");
            return Task.FromResult(new List<Bazaar.Client.Model.ItemPrice>());
        });

        Func<Task> first = () => service.GetBazaarPrices();
        await first.Should().ThrowAsync<System.Net.Http.HttpRequestException>();
        (await service.GetBazaarPrices()).Should().NotBeNull();

        calls.Should().Be(2);
    }
}
