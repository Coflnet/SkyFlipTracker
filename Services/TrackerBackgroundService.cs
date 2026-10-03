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
using System.Collections.Generic;

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
        internal static Prometheus.Counter consumeErrors = Prometheus.Metrics.CreateCounter("sky_fliptracker_consume_errors_total", "Counts messages that could not be processed and were skipped", "topic");
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

        /// <summary>
        /// Exceptions that say the store/network is unavailable, not that the message is bad.
        /// Those must be retried instead of skipping the message.
        /// </summary>
        internal static bool IsInfrastructureError(Exception e)
        {
            switch (e)
            {
                case null:
                    return false;
                case AggregateException agg:
                    return agg.InnerExceptions.Any(IsInfrastructureError);
                case TimeoutException:
                case OperationCanceledException:
                case System.Net.Http.HttpRequestException:
                case System.Net.Sockets.SocketException:
                case System.IO.IOException:
                case KafkaException:
                case System.Data.Common.DbException:
                case global::Cassandra.NoHostAvailableException:
                case global::Cassandra.OperationTimedOutException:
                case global::Cassandra.QueryExecutionException:
                    return true;
            }
            return IsInfrastructureError(e.InnerException);
        }

        private static string Describe(object item)
        {
            return item switch
            {
                SaveAuction a => $"{a.Uuid} {a.Tag}",
                LowPricedAuction lp => $"{lp.Auction?.Uuid} {lp.Auction?.Tag}",
                _ => Truncate(JsonConvert.SerializeObject(item))
            };
        }

        private static string Truncate(string text) => text.Length > 500 ? text.Substring(0, 500) : text;

        /// <summary>
        /// Processes a consumed batch so one bad message can never stop the consumer.
        /// Infrastructure errors are retried with backoff (never skipped), a message that fails
        /// deterministically is logged, counted and skipped so its offset gets committed.
        /// </summary>
        internal static TimeSpan MaxInfrastructureRetry = TimeSpan.FromMinutes(10);

        internal static async Task ProcessResilient<T>(IReadOnlyCollection<T> items, Func<IEnumerable<T>, Task> process, string topic,
            ILogger logger, CancellationToken stoppingToken, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            delay ??= Task.Delay;
            async Task<Exception> TryProcess(IReadOnlyCollection<T> toProcess)
            {
                var backoff = TimeSpan.FromSeconds(1);
                var started = DateTime.UtcNow;
                while (true)
                {
                    try
                    {
                        await process(toProcess);
                        return null;
                    }
                    // a store that stays unreachable ends the consumer (and with it the host) so the restart and its alert show it
                    catch (Exception e) when (IsInfrastructureError(e) && !stoppingToken.IsCancellationRequested && DateTime.UtcNow - started < MaxInfrastructureRetry)
                    {
                        logger.LogError(e, "store unavailable while consuming {topic}, retrying in {backoff}", topic, backoff);
                        await delay(backoff, stoppingToken);
                        if (backoff < TimeSpan.FromSeconds(30))
                            backoff *= 2;
                    }
                    catch (Exception e) when (!IsInfrastructureError(e))
                    {
                        return e;
                    }
                }
            }
            void Skip(IEnumerable<T> bad, Exception e)
            {
                logger.LogError(e, "This sell caused error on {topic}, skipping: {item}", topic, string.Join(',', bad.Select(i => Describe(i))));
                consumeErrors.WithLabels(topic).Inc();
            }

            var error = await TryProcess(items);
            if (error == null)
                return;
            if (items.Count == 1)
            {
                Skip(items, error);
                return;
            }
            // find out which messages are bad, process the rest one by one
            foreach (var item in items)
            {
                var itemError = await TryProcess(new[] { item });
                if (itemError != null)
                    Skip(new[] { item }, itemError);
            }
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
                await ProcessResilient(elements.ToList(), async batch =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                    await NewMethod(batch, service);
                }, config[topicName], logger, stoppingToken);
            }, stoppingToken, "sky-fliptracker", batchSize);
        }

        private async Task NewAuctions(CancellationToken stoppingToken)
        {
            await KafkaConsumer.ConsumeBatch<SaveAuction>(config, config["TOPICS:NEW_AUCTION"], async toUpdate =>
            {
                foreach (var item in toUpdate)
                {
                    if (item.StartingBid > 5_000_000 && item.Start > DateTime.UtcNow - TimeSpan.FromMinutes(1))
                        CheckLister(item); // expensive items may be underlisted
                    var coop = item.Coop;
                    if (coop == null || !coop.Any(c => AnalyseController.BadPlayersList.Contains(c)))
                    {
                        continue;
                    }
                    foreach (var uuid in coop)
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
                await Task.Delay(35_000);
                try
                {
                    logger.LogInformation("requesting ah update for {auctioneedr} because of {uuid}", item.AuctioneerId, item.Uuid);
                    await rerequestService.BaseAhPlayerIdPostAsync(item.AuctioneerId, "checkLister");
                }
                catch (Exception e)
                {
                    logger.LogError(e, "could not rerequest player auctions");
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
                await ProcessResilient(sells.ToList(), async batch =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                    await service.AddSells(batch);
                    consumedSells.Inc(batch.Count());
                    await service.PutBuySpeedOnBoard(batch);
                }, config["TOPICS:SOLD_AUCTION"], logger, stoppingToken);
            }, stoppingToken, 50);
            await KafkaConsumer.ConsumeBatch<SaveAuction>(consumeConfig, config["TOPICS:SOLD_AUCTION"], async flipEvents =>
            {
                if (flipEvents.All(e => e.End < DateTime.UtcNow - TimeSpan.FromDays(2)))
                {
                    logger.LogInformation("skipping old sell");
                    return;
                }
                // Fully await the indexing before returning. Previously this raced work() against a
                // 5s timer and returned the faster one; when a batch took longer than 5s (common for
                // slow profit calculations like pets with added xp or talisman/craft upgrades, which do
                // extra price/craft lookups) the handler returned, the Kafka offset was committed, and
                // work() kept running orphaned - so a rebalance or pod restart before it finished dropped
                // those flips permanently. The consumer's SessionTimeoutMs (65s) and IndexCassandra's own
                // 20s cancellation bound the wait, so awaiting to completion is safe.
                var recent = flipEvents.Where(e => e.End > DateTime.UtcNow - TimeSpan.FromDays(5)).ToList();
                await ProcessResilient(recent, async batch =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                    await service.IndexCassandra(batch);
                }, config["TOPICS:SOLD_AUCTION"], logger, stoppingToken);
            }, stoppingToken, 32);
            throw new Exception("consuming sells stopped");
        }
        private async Task LoadFlip(CancellationToken stoppingToken)
        {
            await KafkaConsumer.ConsumeBatch<SaveAuction>(config, config["TOPICS:LOAD_FLIPS"], async toUpdate =>
            {
                await ProcessResilient(toUpdate.ToList(), async batch =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                    await service.IndexCassandra(batch);
                    flipsUpdated.Inc(batch.Count());
                    Console.WriteLine("updated flips " + batch.Count());
                }, config["TOPICS:LOAD_FLIPS"], logger, stoppingToken);
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

                consumeCounter.Inc(lps.Count());
                if (lps.All(lp => lp.Auction.End < DateTime.UtcNow))
                    return;
                await ProcessResilient(lps.ToList(), async batch =>
                {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                try
                {
                    await Recheck(batch, scope, service);
                }
                catch (System.Exception e)
                {
                    logger.LogError(e, "could not rerequest player auctions");
                }
                await StoreContext(batch, scope);
                await Task.Delay(3000); // wait for db to store potential double sales in the same minute
                await service.AddFlips(batch.DistinctBy(lp => lp.UId + (int)lp.Finder + lp.TargetPrice).Select(lp => new Flip()
                {
                    AuctionId = lp.UId,
                    FinderType = lp.Finder,
                    TargetPrice = (int)(int.MaxValue > lp.TargetPrice ? lp.TargetPrice : int.MaxValue)
                }));
                }, config["TOPICS:LOW_PRICED"], logger, stoppingToken);
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
                    var unsoldTask = storageService.SaveUnsoldFlip(new(lp));
                    await storageService.SaveFinderContext(lp);
                    await unsoldTask;
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
                                if (profit > 300_000)
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
