using System;
using Coflnet.Sky.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Consumer configuration with a hard cap on librdkafka's read-ahead memory.
/// The client defaults (queued.max.messages.kbytes=64MB and fetch.max.bytes=50MB per consumer) multiply with
/// the seven consumers of this service and fill up within seconds whenever a consumer is assigned partitions with a backlog
/// (restart or rebalance after the other replica died). That memory is native, the managed GC limit does not see it.
/// </summary>
public static class BoundedKafkaConfig
{
    /// <summary>Read-ahead queue per consumer in kilobytes</summary>
    public const int DefaultQueuedMaxKbytes = 4096;
    /// <summary>Largest single fetch response per consumer in bytes</summary>
    public const int DefaultFetchMaxBytes = 4 * 1024 * 1024;
    /// <summary>Largest amount fetched per partition and request in bytes</summary>
    public const int DefaultPartitionFetchBytes = 1024 * 1024;

    /// <summary>Time one batch may take on top of the infrastructure retry window before the poll interval is exceeded</summary>
    public static readonly TimeSpan PollIntervalMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Same settings as the shared <c>KafkaConsumer.ConsumeBatch</c> overload that takes an <see cref="IConfiguration"/>, plus the memory bounds.
    /// </summary>
    public static ConsumerConfig Create(IConfiguration config, string groupId, AutoOffsetReset start = AutoOffsetReset.Earliest)
    {
        return Bound(new ConsumerConfig(KafkaCreator.GetClientConfig(config))
        {
            GroupId = groupId,
            AutoOffsetReset = start,
            EnableAutoCommit = false,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
        }, config);
    }

    /// <summary>
    /// The poll interval that outlasts the infrastructure retry window of <c>ProcessResilient</c> plus one batch
    /// </summary>
    public static int MaxPollIntervalMs() => (int)(TrackerBackgroundService.MaxInfrastructureRetry + PollIntervalMargin).TotalMilliseconds;

    /// <summary>
    /// Applies the memory bounds to an existing config. <c>KAFKA_CONSUMER_QUEUE_KBYTES</c> overrides the read-ahead size.
    /// </summary>
    public static ConsumerConfig Bound(ConsumerConfig consumerConfig, IConfiguration config = null)
    {
        var queueKbytes = DefaultQueuedMaxKbytes;
        if (int.TryParse(config?["KAFKA_CONSUMER_QUEUE_KBYTES"], out var configured) && configured > 0)
            queueKbytes = configured;
        consumerConfig.QueuedMaxMessagesKbytes = queueKbytes;
        consumerConfig.QueuedMinMessages = 1000;
        consumerConfig.FetchMaxBytes = DefaultFetchMaxBytes;
        consumerConfig.MaxPartitionFetchBytes = DefaultPartitionFetchBytes;
        // batches are processed inside the poll loop and retried for up to MaxInfrastructureRetry, the default 300s would evict the member in the middle of that
        consumerConfig.MaxPollIntervalMs = MaxPollIntervalMs();
        return consumerConfig;
    }
}
