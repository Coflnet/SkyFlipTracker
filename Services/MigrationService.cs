using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Coflnet.Cassandra;
using Coflnet.Sky.SkyAuctionTracker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class MigrationService : BackgroundService
{
    private ISession session;
    private ISession oldSession;
    private ILogger<MigrationService> logger;
    private ConnectionMultiplexer redis;
    // get di
    private IServiceProvider serviceProvider;
    private IConfiguration config;
    private FlipStorageService flipStorageService;
    public bool IsDone { get; private set; }

    public MigrationService(ISession session, OldSession oldSession, ILogger<MigrationService> logger, ConnectionMultiplexer redis, IServiceProvider serviceProvider, IConfiguration config, FlipStorageService flipStorageService)
    {
        this.session = session;
        this.logger = logger;
        this.redis = redis;
        this.serviceProvider = serviceProvider;
        this.oldSession = oldSession.Session;
        this.config = config;
        this.flipStorageService = flipStorageService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await flipStorageService.Migrate();
        var handlerLogger = serviceProvider.GetRequiredService<ILogger<MigrationHandler<PastFlip>>>();
        var migrationHandler = new MigrationHandler<PastFlip>(
                () => flipStorageService.GetTable(oldSession),
                session, handlerLogger, redis,
                () => flipStorageService.GetTable(session));
        await migrationHandler.Migrate(stoppingToken);
        logger.LogInformation("Migrated all data");
        IsDone = true;
    }
}