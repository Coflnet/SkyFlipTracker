global using System;

using Coflnet.Sky.SkyAuctionTracker.Models;
using Cassandra;
using Cassandra.Mapping;
using Cassandra.Data.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Cassandra.Mapping.TypeConversion;
using Cassandra.Mapping.Utils;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.SkyAuctionTracker.Services;
/// <summary>
/// Stores pastflips in cassandra
/// </summary>
public class FlipStorageService
{
    private SemaphoreSlim sessionOpenLock = new SemaphoreSlim(1);
    private ILogger<FlipStorageService> logger;
    private IConfiguration config;
    private Table<OutspedFlip> outspedTable;
    private Table<FinderContext> finderContexts;
    private ISession session;
    public FlipStorageService(ILogger<FlipStorageService> logger, IConfiguration config, ISession session)
    {
        this.logger = logger;
        this.config = config;
        this.session = session;
    }

    public async Task<ISession> GetSession(string keyspace = null)
    {
        return session;
    }

    public async Task SaveFlip(PastFlip flip)
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        try
        {
            await table.Insert(flip).ExecuteAsync();
        }
        catch (InvalidQueryException e)
        {
            if (e.Message.Contains("No keyspace has been specified"))
            {
                session.ChangeKeyspace(config["CASSANDRA:KEYSPACE"]);
            }
            throw e;
        }
    }

    public async Task SaveFinderContext(LowPricedAuction flip)
    {
        var context = new FinderContext
        {
            AuctionId = Guid.Parse(flip.Auction.Uuid),
            Finder = flip.Finder,
            FoundTime = DateTime.UtcNow,
            Context = flip.AdditionalProps,
            AuctionContext = flip.Auction.Context
        };
        await finderContexts.Insert(context).ExecuteAsync();
    }

    public async Task SaveOutspedFlip(string itemTag, string key, Guid trigger)
    {
        await outspedTable.Insert(new OutspedFlip { ItemTag = itemTag, Key = key, TriggeredBy = trigger, Time = DateTime.UtcNow }).ExecuteAsync();
    }

    public async Task<IEnumerable<OutspedFlip>> GetOutspedFlips()
    {
        return await outspedTable.ExecuteAsync();
    }

    public async Task<IEnumerable<FinderContext>> GetFinderContexts(Guid auctionId)
    {
        return await finderContexts.Where(f => f.AuctionId == auctionId).ExecuteAsync();
    }

    public async Task SaveFlips(IEnumerable<PastFlip> flips)
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        await Task.WhenAll(flips.Select(f => table.Insert(f).ExecuteAsync()));
    }

    public async Task<IEnumerable<PastFlip>> GetFlips(Guid flipper, DateTime start, DateTime end)
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        return await table.Where(f => f.Flipper == flipper && f.SellTime >= start && f.SellTime <= end).ExecuteAsync();
    }
    public async Task<PastFlip> GetFlip(Guid flipper, long uid)
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        var end = DateTime.UtcNow;
        var start = end.AddDays(-10000);
        return await table.Where(f => f.Flipper == flipper && f.Uid == uid && f.SellTime > start && f.SellTime < end)
                .AllowFiltering() // bad
                .ExecuteAsync().ContinueWith(t => t.Result.FirstOrDefault());
    }

    public async Task<IEnumerable<(Guid, short)>> GetFlipVersions(Guid flipper, DateTime start, DateTime end, IEnumerable<Guid> auctionIds)
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        var asObject = await table.Where(f => f.Flipper == flipper && f.SellTime >= start && f.SellTime <= end)
            .Select(f => new { f.SellAuctionId, f.Version }).ExecuteAsync();
        return asObject.Select(f => (f.SellAuctionId, f.Version));
    }

    public async Task<long> GetProfit(Guid flipper, DateTime end, DateTime start)
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        var result = await table.Where(f => f.Flipper == flipper && f.SellTime >= start && f.SellTime <= end).Select(f => f.Profit).ExecuteAsync();
        return result.Sum();
    }

    private static Table<PastFlip> GetFlipsTable(ISession session)
    {
        return new Table<PastFlip>(session, new MappingConfiguration().Define(new Map<PastFlip>()
            .PartitionKey(c => c.Flipper)
            .ClusteringKey(c => c.SellTime, SortOrder.Descending)
            .ClusteringKey(c => c.Uid)
            .Column(c => c.FinderType, cm => cm.WithDbType<int>())
            .Column(c => c.ItemTier, cm => cm.WithDbType<int>())
            .Column(c => c.ProfitChanges, cm => cm.Ignore())
            .Column(o => o.Flags, c => c.WithName("flags").WithDbType<int>())
            ), "flips");
    }

    public Table<PastFlip> GetTable(ISession session) => GetFlipsTable(session);

    /// <summary>
    /// Brings the db into the current state
    /// </summary>
    /// <returns></returns>
    public async Task Migrate()
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        await table.CreateIfNotExistsAsync();
        finderContexts = new Table<FinderContext>(session, new MappingConfiguration().Define(new Map<FinderContext>()
            .PartitionKey(c => c.AuctionId)
            .ClusteringKey(c => c.Finder)
            .Column(c => c.AuctionContext, cm => cm.WithDbType<Dictionary<string, string>>().WithName("auction_context"))
            .Column(c => c.Context, cm => cm.WithDbType<Dictionary<string, string>>())
            .Column(c => c.FoundTime, cm => cm.WithDbType<DateTime>().WithName("found_time"))
            .Column(c => c.Finder, cm => cm.WithDbType<int>())
            .Column(c=>c.AuctionId, cm=>cm.WithName("auction_id").WithDbType<Guid>())
            ), "finder_context");
        // set the table to have a ttl of 14 days and time window compaction
      //  session.Execute("CREATE TABLE IF NOT EXISTS finder_context (auction_id uuid, finder int, auction_context map<text, text>, context map<text, text>, found_time timestamp, PRIMARY KEY (auction_id, finder))"
      //   + " WITH default_time_to_live = 1209600 AND compaction = { 'class' : 'TimeWindowCompactionStrategy', 'compaction_window_size' : 1, 'compaction_window_unit' : 'DAYS' }");
        
        // alter table change finder to int
        session.Execute("ALTER TABLE finder_context ALTER finder TYPE int");
        outspedTable = new Table<OutspedFlip>(session, new MappingConfiguration().Define(new Map<OutspedFlip>()
            .PartitionKey(c => c.ItemTag)
            .ClusteringKey(c => c.Key)
            .Column(c=>c.TriggeredBy)
            .Column(c=>c.ItemTag, cm=>cm.WithName("item_tag"))
            .Column(c=>c.TriggeredBy, cm=>cm.WithName("triggered_by"))
            .Column(c=>c.Time)), "outsped_flips");
        // set ttl to 30 days and time window compaction
      //  session.Execute("CREATE TABLE IF NOT EXISTS outsped_flips (item_tag text, key text, triggered_by uuid, time timestamp, PRIMARY KEY (item_tag, key))"
      //   + " WITH default_time_to_live = 2592000 AND compaction = { 'class' : 'TimeWindowCompactionStrategy', 'compaction_window_size' : 1, 'compaction_window_unit' : 'DAYS' }");

    }
}
