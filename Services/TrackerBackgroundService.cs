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
                var storageService = scope.ServiceProvider.GetRequiredService<FlipStorageService>();
                await storageService.Migrate();
            }

            Task flipCons = ConsumeFlips(stoppingToken);
            Task flipEventCons = ConsumeEvents(stoppingToken);
            var sellCons = SoldAuction(stoppingToken);
            var newAuctions = NewAuctions(stoppingToken);

            await Task.WhenAny(
                Run(flipCons, "consuming flips"),
                Run(flipEventCons, "flip events cons"),
                Run(sellCons, "sells con"),
                Run(LoadFlip(stoppingToken), "load flip"),
                Run(newAuctions, "new auctions"));
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
        private async Task NewAuctions(CancellationToken stoppingToken)
        {
            await KafkaConsumer.ConsumeBatch<SaveAuction>(config, config["TOPICS:NEW_AUCTION"], toUpdate =>
            {
                foreach (var item in toUpdate)
                {
                    if (!item.Coop.Any(c => AnalyseController.BadPlayersList.Contains(c)))
                    {
                        continue;
                    }
                    foreach (var uuid in item.Coop)
                    {
                        if (!AnalyseController.BadPlayersList.Contains(uuid))
                            logger.LogWarning("found bad player in coop {uuid} from {auctioneer}", uuid, item.AuctioneerId);
                        AnalyseController.BadPlayersList.Add(uuid);
                    }
                }
                return Task.CompletedTask;
            }, stoppingToken, "sky-fliptracker", 8);
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

            var sellConsume = KafkaConsumer.ConsumeBatch<SaveAuction>(sellConsumeConfig, config["TOPICS:SOLD_AUCTION"], async sells =>
            {
                if (sells.All(e => e.End < DateTime.UtcNow - TimeSpan.FromHours(8)))
                {
                    if (Random.Shared.NextDouble() < 0.1)
                        logger.LogInformation("skipping old sell");
                    return;
                }
                for (int i = 0; i < 3; i++)
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                        await service.AddSells(sells);
                        consumedSells.Inc(sells.Count());
                        await service.PutBuySpeedOnBoard(sells);
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
                if (flipEvents.All(e => e.End < DateTime.UtcNow - TimeSpan.FromDays(5)))
                {
                    logger.LogInformation("skipping old sell");
                    return;
                }
                for (int i = 0; i < 3; i++)
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                        await service.IndexCassandra(flipEvents);
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
                    await Recheck(lps, scope, service);
                }
                catch (System.Exception e)
                {
                    logger.LogError(e, "could not rerequest player auctions");
                }
            }, stoppingToken, "sky-fliptracker", 50);
        }

        private async Task Recheck(IEnumerable<LowPricedAuction> lps, IServiceScope scope, TrackerService service)
        {
            var rerequestService = scope.ServiceProvider.GetRequiredService<IBaseApi>();
            var events = new List<FlipEvent>();
            foreach (var item in lps.Where(lp => lp.TargetPrice - lp.Auction.StartingBid > 550_000)
                .GroupBy(lp => lp.Auction.UId).Select(g => g.First()))
            {
                if (item.Auction.Start > DateTime.UtcNow - TimeSpan.FromMinutes(1))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var purchaseableIn = DateTime.UtcNow - item.Auction.Start;
                            if (purchaseableIn > TimeSpan.FromSeconds(1))
                                await Task.Delay(purchaseableIn);
                            try
                            {
                                await rerequestService.BaseAhPlayerIdPostAsync(item.Auction.AuctioneerId, "recheck");
                            }
                            catch (Exception)
                            {
                                await Task.Delay(500);
                                await rerequestService.BaseAhPlayerIdPostAsync(item.Auction.AuctioneerId, "recheck2");
                            }
                        }
                        catch (System.Exception e)
                        {
                            logger.LogError(e, "could not rerequest player auctions");
                        }
                    });
                }

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
    }
}
