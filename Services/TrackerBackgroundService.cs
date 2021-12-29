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

namespace Coflnet.Sky.SkyAuctionTracker.Services
{

    public class TrackerBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;
        private ILogger<TrackerBackgroundService> logger;

        private static Prometheus.Counter consumeCounter = Prometheus.Metrics.CreateCounter("sky_fliptracker_consume_lp","Counts the consumed low priced auctions");

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

            var flipCons = Coflnet.Kafka.KafkaConsumer.Consume<LowPricedAuction>(config["KAFKA_HOST"], config["TOPICS:LOW_PRICED"], async lp =>
            {
                var service = GetService();
                consumeCounter.Inc();
                await service.AddFlip(new Flip()
                {
                    AuctionId = lp.UId,
                    FinderType = lp.Finder,
                    TargetPrice = lp.TargetPrice
                });
            }, stoppingToken, "fliptracker");

            var flipEventCons = Coflnet.Kafka.KafkaConsumer.Consume<FlipEvent>(config["KAFKA_HOST"], config["TOPICS:FLIP_EVENT"], async flipEvent =>
            {
                TrackerService service = GetService();
                await service.AddEvent(flipEvent);
            }, stoppingToken, "fliptracker");


            await Task.WhenAny(flipCons, flipEventCons);
            logger.LogError("consuming stopped :O");
        }

        private TrackerService GetService()
        {
            return scopeFactory.CreateScope().ServiceProvider.GetRequiredService<TrackerService>();
        }
    }
}