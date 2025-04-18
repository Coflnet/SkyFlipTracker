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
using Coflnet.Sky.Proxy.Client.Api;
using Coflnet.Sky.Kafka;
using Newtonsoft.Json;

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
                var storageService = scope.ServiceProvider.GetRequiredService<FlipStorageService>();
                var context = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
                // make sure all migrations are applied
                await context.Database.MigrateAsync();
                await storageService.Migrate();
            }

            Task flipCons = ConsumeFlips(stoppingToken);
            Task flipEventCons = ConsumeEvents(stoppingToken);
            var sellCons = SoldAuction(stoppingToken);
            var newAuctions = NewAuctions(stoppingToken);

            await Task.WhenAny(
                Run(ConsumePlayerTrades(stoppingToken), "consuming trades"),
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
            await ConsumeTryCatch<FlipEvent>(async (flipEvents, service) =>
            {
                await service.AddEvents(flipEvents);
                consumeEvent.Inc(flipEvents.Count());
            }, "TOPICS:FLIP_EVENT", 15, stoppingToken);
        }

        private async Task ConsumePlayerTrades(CancellationToken stoppingToken)
        {
            await ConsumeTryCatch<TradeModel>(async (trades, service) =>
            {
                await service.AddTrades(trades);
            }, "TOPICS:PLAYER_TRADE", 1, stoppingToken);
        }

        private async Task ConsumeTryCatch<T>(Func<IEnumerable<T>, TrackerService, Task> NewMethod, string topicName, int batchSize, CancellationToken stoppingToken)
        {
            await KafkaConsumer.ConsumeBatch<T>(config, config[topicName], async elements =>
            {
                for (int i = 0; i < 3; i++)
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                        await NewMethod(elements, service);
                        return;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "could not from " + topicName);
                    }
            }, stoppingToken, "sky-fliptracker", batchSize);
        }

        private async Task NewAuctions(CancellationToken stoppingToken)
        {
            await KafkaConsumer.ConsumeBatch<SaveAuction>(config, config["TOPICS:NEW_AUCTION"], async toUpdate =>
            {
                foreach (var item in toUpdate)
                {
                    if (item.StartingBid > 80_000_000 && item.Start > DateTime.UtcNow - TimeSpan.FromMinutes(1))
                        CheckLister(item); // expensive items may be underlisted
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
            }, stoppingToken, "sky-fliptracker", 40, AutoOffsetReset.Latest);
        }

        private void CheckLister(SaveAuction item)
        {
            Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var rerequestService = scope.ServiceProvider.GetRequiredService<IBaseApi>();
                var purchaseAble = item.Start + TimeSpan.FromSeconds(19) - DateTime.UtcNow;
                if (purchaseAble > TimeSpan.FromSeconds(1))
                    await Task.Delay(purchaseAble);
                await Task.Delay(45_000);
                for (int i = 0; i < 2; i++)
                {
                    if (i == 2)
                    {
                        await Task.Delay(10000);
                        continue; // normal update
                    }
                    try
                    {
                        logger.LogInformation("requesting ah update for {auctioneedr} because of {uuid}", item.AuctioneerId, item.Uuid);
                        await rerequestService.BaseAhPlayerIdPostAsync(item.AuctioneerId, "checkLister" + i);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "could not rerequest player auctions");
                    }
                    await Task.Delay(60_000);
                }
            });
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
            var consumeError = false;
            await KafkaConsumer.ConsumeBatch<SaveAuction>(consumeConfig, config["TOPICS:SOLD_AUCTION"], async flipEvents =>
            {
                if (flipEvents.All(e => e.End < DateTime.UtcNow - TimeSpan.FromDays(2)))
                {
                    logger.LogInformation("skipping old sell");
                    return;
                }
                var work = async () =>
                {
                    for (int i = 0; i < 3; i++)
                        try
                        {
                            using var scope = scopeFactory.CreateScope();
                            var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                            await service.IndexCassandra(flipEvents.Where(e => e.End > DateTime.UtcNow - TimeSpan.FromDays(5)));
                            return;
                        }
                        catch (Exception e)
                        {
                            logger.LogError(e, "could not save event once");
                            consumeError = true;
                            await Task.Delay(1000);
                        }
                };
                await Task.WhenAny(work(), Task.Delay(TimeSpan.FromSeconds(5)));
                if (consumeError)
                {
                    logger.LogInformation("cassanra index backoff");
                    await Task.Delay(20_000);
                    consumeError = false;
                }
            }, stoppingToken, 32);
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
                if (lps.All(lp => lp.Auction.End < DateTime.UtcNow - TimeSpan.FromDays(4)))
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
                if (lps.All(lp => lp.Auction.End < DateTime.UtcNow))
                    return;
                try
                {
                    await Recheck(lps, scope, service);
                }
                catch (System.Exception e)
                {
                    logger.LogError(e, "could not rerequest player auctions");
                }
                await StoreContext(lps, scope);
            }, stoppingToken, "sky-fliptracker", 50);
        }

        private async Task StoreContext(IEnumerable<LowPricedAuction> lps, IServiceScope scope)
        {
            var storageService = scope.ServiceProvider.GetRequiredService<FlipStorageService>();
            // parallelize this
            await Parallel.ForEachAsync(lps, new ParallelOptions() { MaxDegreeOfParallelism = 2 }, async (lp, c) =>
            {
                try
                {
                    await storageService.SaveFinderContext(lp);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "could not save low priced auction context {a} {context}", JsonConvert.SerializeObject(lp), JsonConvert.SerializeObject(lp.Auction.Context));
                }
            });
        }

        private async Task Recheck(IEnumerable<LowPricedAuction> lps, IServiceScope scope, TrackerService service)
        {
            var rerequestService = scope.ServiceProvider.GetRequiredService<IBaseApi>();
            var events = new List<FlipEvent>();
            foreach (var item in lps.Where(lp => lp.TargetPrice - lp.Auction.StartingBid > 8050_000)
                .GroupBy(lp => lp.Auction.UId).Select(g => g.First()))
            {
                if (item.Auction.Start > DateTime.UtcNow - TimeSpan.FromMinutes(1))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var profit = item.TargetPrice - item.Auction.StartingBid;
                            var purchaseableIn = DateTime.UtcNow - item.Auction.Start;
                            if (purchaseableIn > TimeSpan.FromSeconds(1))
                                await Task.Delay(purchaseableIn);
                            try
                            {
                                if (profit > 3_000_000)
                                    await rerequestService.BaseAhPlayerIdPostAsync(item.Auction.AuctioneerId, "recheck");
                                await Task.Delay(40_000);
                                await rerequestService.BaseAhPlayerIdPostAsync(item.Auction.AuctioneerId, "recheck40");
                                await Task.Delay(60_000);
                                if (profit > 3_000_000)
                                    await rerequestService.BaseAhPlayerIdPostAsync(item.Auction.AuctioneerId, "recheck100");
                                await Task.Delay(60_000);
                                if (profit > 6_000_000)
                                    await rerequestService.BaseAhPlayerIdPostAsync(item.Auction.AuctioneerId, "recheck160");
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
