using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using System;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.SkyAuctionTracker.Controllers;
using System.Linq;
using Coflnet.Sky.Core;
using Coflnet.Kafka;
using Coflnet.Sky.Proxy.Client.Api;

namespace Coflnet.Sky.SkyAuctionTracker.Services
{

    public class TrackerBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;
        private ILogger<TrackerBackgroundService> logger;

        private static Prometheus.Counter consumeCounter = Prometheus.Metrics.CreateCounter("sky_fliptracker_consume_lp", "Counts the consumed low priced auctions");
        private static Prometheus.Counter consumeEvent = Prometheus.Metrics.CreateCounter("sky_fliptracker_consume_event", "Counts the consumed flip events");
        private static Prometheus.Counter consumedSells = Prometheus.Metrics.CreateCounter("sky_fliptracker_consume_sells", "Counts the consumed sells");
        private static Prometheus.Counter flipsUpdated = Prometheus.Metrics.CreateCounter("sky_fliptracker_flips_updated", "How many flips were updated");
        private KafkaCreator kafkaCreator;
        public TrackerBackgroundService(
            IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<TrackerBackgroundService> logger, KafkaCreator kafkaCreator)
        {
            this.scopeFactory = scopeFactory;
            this.config = config;
            this.logger = logger;
            this.kafkaCreator = kafkaCreator;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (var scope = scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
                // make sure all migrations are applied
                await context.Database.MigrateAsync();
            }

            Task flipCons = ConsumeFlips(stoppingToken);
            Task flipEventCons = ConsumeEvents(stoppingToken);
            var sellCons = SoldAuction(stoppingToken);

            await Task.WhenAny(
                Run(flipCons, "consuming flips"),
                Run(flipEventCons, "flip events cons"),
                Run(sellCons, "sells con"),
                Run(LoadFlip(stoppingToken), "load flip"));
            logger.LogError("consuming stopped :O");
            if (!stoppingToken.IsCancellationRequested)
                throw new Exception("at least one consuming process stopped");
        }

        private async Task Run(Task task, string message)
        {
            try
            {
                await task;
            }
            catch (System.Exception e)
            {
                logger.LogError(e, message);
                throw;
            }
        }

        private async Task ConsumeEvents(CancellationToken stoppingToken)
        {
            await Coflnet.Kafka.KafkaConsumer.ConsumeBatch<FlipEvent>(config, config["TOPICS:FLIP_EVENT"], async flipEvents =>
            {
                for (int i = 0; i < 3; i++)
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                        await service.AddEvents(flipEvents);
                        consumeEvent.Inc(flipEvents.Count());
                        return;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "could not save event once");
                    }
            }, stoppingToken, "sky-fliptracker", 15);
        }
        private async Task SoldAuction(CancellationToken stoppingToken)
        {

            var consumeConfig = new ConsumerConfig(KafkaCreator.GetClientConfig(config))
            {
                GroupId = "sky-fliptracker",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                SessionTimeoutMs = 65000,
                AutoCommitIntervalMs = 0,
                PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
            };
            var sellConsumeConfig = new ConsumerConfig(consumeConfig.ToDictionary(c => c.Key, c => c.Value))
            {
                GroupId = "sky-fliptracker-sell",
                SessionTimeoutMs = 10000,
            };
            await kafkaCreator.CreateTopicIfNotExist(config["TOPICS:SOLD_AUCTION"], 9);

            var sellConsume = KafkaConsumer.ConsumeBatch<SaveAuction>(sellConsumeConfig, config["TOPICS:SOLD_AUCTION"], async flipEvents =>
            {
                /*if (flipEvents.All(e => e.End < DateTime.UtcNow - TimeSpan.FromHours(8)))
                {
                    if (Random.Shared.NextDouble() < 0.1)
                        logger.LogInformation("skipping old sell");
                    return;
                }*/
                for (int i = 0; i < 3; i++)
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                        await service.AddSells(flipEvents);
                        consumedSells.Inc(flipEvents.Count());
                        return;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "could not save sells once");
                        await Task.Delay(1000);
                    }
            }, stoppingToken, 50);
            await KafkaConsumer.ConsumeBatch<SaveAuction>(consumeConfig, config["TOPICS:SOLD_AUCTION"], async flipEvents =>
            {
                if (flipEvents.All(e => e.End < DateTime.UtcNow - TimeSpan.FromDays(2)))
                {
                    logger.LogInformation("skipping old sell");
                    return;
                }
                for (int i = 0; i < 3; i++)
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                        return;
                        await service.IndexCassandra(flipEvents);
                        consumeEvent.Inc(flipEvents.Count());
                        return;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "could not save event once");
                        await Task.Delay(1000);
                    }
            }, stoppingToken, 20);
            throw new Exception("consuming sells stopped");
        }
        private async Task LoadFlip(CancellationToken stoppingToken)
        {
            await KafkaConsumer.ConsumeBatch<SaveAuction>(config, config["TOPICS:LOAD_FLIPS"], async toUpdate =>
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                await service.IndexCassandra(toUpdate);
                flipsUpdated.Inc(toUpdate.Count());
                Console.WriteLine("updated flips " + toUpdate.Count());
            }, stoppingToken, "sky-fliptracker", 8);
        }

        private async Task ConsumeFlips(CancellationToken stoppingToken)
        {
            await KafkaConsumer.ConsumeBatch<LowPricedAuction>(config, config["TOPICS:LOW_PRICED"], async lps =>
            {
                if (lps.Count() == 0)
                    return;

                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                await service.AddFlips(lps.DistinctBy(lp => lp.UId + (int)lp.Finder + lp.TargetPrice).Select(lp => new Flip()
                {
                    AuctionId = lp.UId,
                    FinderType = lp.Finder,
                    TargetPrice = (int)(int.MaxValue > lp.TargetPrice ? lp.TargetPrice : int.MaxValue)
                }));
                consumeCounter.Inc(lps.Count());
                try
                {
                    var rerequestService = scope.ServiceProvider.GetRequiredService<IBaseApi>();
                    var events = new List<FlipEvent>();
                    foreach (var item in lps.Where(lp => lp.TargetPrice - lp.Auction.StartingBid > 3_000_000))
                    {
                        if (DateTime.Now - item.Auction.Start < TimeSpan.FromSeconds(12))
                            await Task.Delay(4000);
                        await rerequestService.BaseAhPlayerIdPostAsync(item.Auction.AuctioneerId, "recheck");

                        var startTime = new FlipEvent()
                        {
                            AuctionId = item.Auction.UId,
                            PlayerId = service.GetId(item.Auction.AuctioneerId),
                            Timestamp = item.Auction.Start,
                            Type = FlipEventType.START
                        };
                        events.Add(startTime);
                    }
                    if (events.Count > 0)
                        await service.AddEvents(events);
                }
                catch (System.Exception e)
                {
                    logger.LogError(e, "could not rerequest player auctions");
                }
            }, stoppingToken, "sky-fliptracker", 50);
        }

    }
}
