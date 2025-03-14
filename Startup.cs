global using System.Collections.Generic;
global using System.Linq;
using System.Text.Json.Serialization;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Coflnet.Sky.SkyAuctionTracker.Services;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Coflnet.Sky.Crafts.Client.Api;
using Coflnet.Sky.Api.Client.Api;
using Prometheus;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.Proxy.Client.Api;
using Coflnet.Sky.Settings.Client.Api;
using Coflnet.Leaderboard.Client.Api;
using Coflnet.Sky.Core.Services;
using Coflnet.Core;
using Coflnet.Sky.Kafka;
using StackExchange.Redis;
using Coflnet.Sky.Sniper.Client.Api;
using Coflnet.Sky.McConnect.Api;

namespace Coflnet.Sky.SkyAuctionTracker
{
#pragma warning disable CS1591
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        private IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers().AddJsonOptions(options =>
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

            // Replace with your server version and type.
            // Use 'MariaDbServerVersion' for MariaDB.
            // Alternatively, use 'ServerVersion.AutoDetect(connectionString)'.
            // For common usages, see pull request #1233.
            var serverVersion = new MariaDbServerVersion(new Version(Configuration["MARIADB_VERSION"]));

            // Replace 'YourDbContext' with the name of your own DbContext derived class.
            services.AddDbContext<TrackerDbContext>(
                dbContextOptions => dbContextOptions
                    .UseMySql(Configuration["DB_CONNECTION"], serverVersion,
                     opt => opt.CommandTimeout(5))

                    .EnableSensitiveDataLogging() // <-- These two calls are optional but help
                    .EnableDetailedErrors()       // <-- with debugging (remove for production).
            );
            if (Configuration["OLD_CASSANDRA:HOSTS"] != null)
            {
                services.AddHostedService<MigrationService>();
                services.AddSingleton(ConnectionMultiplexer.Connect(Configuration["REDIS_HOST"]));
            }
            else
                services.AddHostedService<TrackerBackgroundService>();
            services.AddSingleton<IAuctionsApi>(conf => new AuctionsApi(Configuration["API_BASE_URL"]));
            services.AddSingleton<IPricesApi>(conf => new PricesApi(Configuration["API_BASE_URL"]));
            services.AddSingleton<IPlayerApi>(conf => new PlayerApi(Configuration["API_BASE_URL"]));
            services.AddSingleton<ICraftsApi>(conf => new CraftsApi(Configuration["CRAFTS_BASE_URL"]));
            services.AddSingleton<IItemsApi>(conf => new ItemsApi(Configuration["ITEMS_BASE_URL"]));
            services.AddSingleton<ISettingsApi>(conf => new SettingsApi(Configuration["SETTINGS_BASE_URL"]));
            services.AddSingleton<IConnectApi>(conf => new ConnectApi(Configuration["MCCONNECT_BASE_URL"]));
            services.AddSingleton<Bazaar.Client.Api.IBazaarApi>(conf => new Bazaar.Client.Api.BazaarApi(Configuration["BAZAAR_BASE_URL"]));
            services.AddSingleton<IScoresApi>(conf => new ScoresApi(Configuration["LEADERBOARD_BASE_URL"]));
            services.AddSingleton<Crafts.Client.Api.IKatApi>(conf => new Crafts.Client.Api.KatApi(Configuration["CRAFTS_BASE_URL"]));
            services.AddSingleton<IBaseApi>(sp => new BaseApi(Configuration["PROXY_BASE_URL"]));
            services.AddSingleton<ISniperApi>(sp => new SniperApi(Configuration["SNIPER_BASE_URL"]));
            services.AddSingleton<ISniperClient, SniperClient>();
            services.AddSingleton<RepresentationConverter>();
            services.AddSingleton<PlayerState.Client.Api.IItemsApi>(sp => new PlayerState.Client.Api.ItemsApi(Configuration["PLAYERSTATE_BASE_URL"]));
            services.AddSingleton<PlayerState.Client.Api.ITransactionApi>(sp => new PlayerState.Client.Api.TransactionApi(Configuration["PLAYERSTATE_BASE_URL"]));

            services.AddSingleton<ProfitChangeService>();
            services.AddSingleton<FlipStorageService>();
            services.AddSingleton<IPriceProviderFactory, PriceProviderFactory>();
            services.AddJaeger(Configuration);
            services.AddTransient<TrackerService>();
            services.AddSingleton<KafkaCreator>();
            services.AddCoflnetCore();
            services.AddSingleton<FlipSumaryEventProducer>();
            services.AddSingleton<HypixelItemService>();
            services.AddHttpClient();
            services.AddResponseCaching();
            services.AddMemoryCache();
            services.AddResponseCompression();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseCoflnetCore();
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            app.UseResponseCompression();
            app.UseResponseCaching();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMetrics();
                endpoints.MapControllers();
            });
        }
    }
}
