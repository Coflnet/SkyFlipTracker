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

namespace Coflnet.Sky.SkyAuctionTracker.Services
{

    public class TrackerBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;
        private ILogger<TrackerBackgroundService> logger;

        private static Prometheus.Counter consumeCounter = Prometheus.Metrics.CreateCounter("sky_fliptracker_consume_lp", "Counts the consumed low priced auctions");
        private static Prometheus.Counter consumeEvent = Prometheus.Metrics.CreateCounter("sky_fliptracker_consume_event", "Counts the consumed flip events");

        public TrackerBackgroundService(
            IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<TrackerBackgroundService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.config = config;
            this.logger = logger;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (var scope = scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
                // make sure all migrations are applied
                await context.Database.MigrateAsync();
            }

            var consConfig = new ConsumerConfig()
            {
                BootstrapServers = config["KAFKA_HOST"],
                GroupId = "flip-tracker"
            };
            Task flipCons = ConsumeFlips(stoppingToken);
            Task flipEventCons = ConsumeEvents(stoppingToken);
            var sellCons = SoldAuction(stoppingToken);

            await Task.WhenAny(
                Run(flipCons, "consuming flips"),
                Run(flipEventCons, "flip events cons"),
                Run(sellCons, "sells con"));
            logger.LogError("consuming stopped :O");
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
            await Coflnet.Kafka.KafkaConsumer.ConsumeBatch<FlipEvent>(config["KAFKA_HOST"], config["TOPICS:FLIP_EVENT"], async flipEvents =>
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
            }, stoppingToken, "fliptracker", 15);
        }
        private async Task SoldAuction(CancellationToken stoppingToken)
        {
            await Coflnet.Kafka.KafkaConsumer.ConsumeBatch<SaveAuction>(config["KAFKA_HOST"], config["TOPICS:SOLD_AUCTION"], async flipEvents =>
            {
                for (int i = 0; i < 3; i++)
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                        await service.AddSells(flipEvents);
                        consumeEvent.Inc(flipEvents.Count());
                        return;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "could not save event once");
                    }
            }, stoppingToken, "fliptracker", 35);
        }

        private async Task ConsumeFlips(CancellationToken stoppingToken)
        {
            await Coflnet.Kafka.KafkaConsumer.ConsumeBatch<LowPricedAuction>(config["KAFKA_HOST"], config["TOPICS:LOW_PRICED"], async lps =>
            {
                if (lps.Count() == 0)
                    return;

                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TrackerService>();
                await service.AddFlips(lps.Select(lp => new Flip()
                {
                    AuctionId = lp.UId,
                    FinderType = lp.Finder,
                    TargetPrice = (int)(int.MaxValue < lp.TargetPrice ? lp.TargetPrice : int.MaxValue)
                }));
                consumeCounter.Inc(lps.Count());
            }, stoppingToken, "fliptracker", 50);
        }

    }
}
