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
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            // make sure all migrations are applied
            await context.Database.MigrateAsync();

            var consConfig = new ConsumerConfig()
            {
                BootstrapServers = config["KAFKA_HOST"],
                GroupId = "flip-tracker"
            };


            var flipCons = Coflnet.Kafka.KafkaConsumer.ConsumeBatch<LowPricedAuction>(config["KAFKA_HOST"], config["TOPICS:LOW_PRICED"], async lps =>
            {
                if (lps.Count() == 0)
                    return;
                await GetService().AddFlips(lps.Select(lp => new Flip()
                {
                    AuctionId = lp.UId,
                    FinderType = lp.Finder,
                    TargetPrice = lp.TargetPrice
                }));
                consumeCounter.Inc(lps.Count());
            }, stoppingToken, "fliptracker", 50);

            var flipEventCons = Coflnet.Kafka.KafkaConsumer.ConsumeBatch<FlipEvent>(config["KAFKA_HOST"], config["TOPICS:FLIP_EVENT"], async flipEvents =>
            {
                for (int i = 0; i < 3; i++)
                    try
                    {
                        await Task.WhenAll(flipEvents.Select(async flipEvent => await GetService().AddEvent(flipEvent)));
                        consumeEvent.Inc(flipEvents.Count());
                        return;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "could not save event once");
                    }
            }, stoppingToken, "fliptracker", 10);


            await Task.WhenAny(flipCons, flipEventCons);
            logger.LogError("consuming stopped :O");
        }

        private TrackerService GetService()
        {
            return scopeFactory.CreateScope().ServiceProvider.GetRequiredService<TrackerService>();
        }
    }
}