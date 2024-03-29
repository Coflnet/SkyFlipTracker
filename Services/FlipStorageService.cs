global using System;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

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

/// <summary>
/// Stores pastflips in cassandra
/// </summary>
public class FlipStorageService
{
    ISession _session;
    private SemaphoreSlim sessionOpenLock = new SemaphoreSlim(1);
    private ILogger<FlipStorageService> logger;
    private IConfiguration config;
    public FlipStorageService(ILogger<FlipStorageService> logger, IConfiguration config)
    {
        this.logger = logger;
        this.config = config;
    }

    public async Task<ISession> GetSession(string keyspace = null)
    {
        if (_session != null)
            return _session;
        await sessionOpenLock.WaitAsync();
        if (_session != null)
            return _session;
        if (keyspace == null)
            keyspace = config["CASSANDRA:KEYSPACE"];
        try
        {
            var cluster = Cluster.Builder()
                                .WithCredentials(config["CASSANDRA:USER"], config["CASSANDRA:PASSWORD"])
                                .AddContactPoints(config["CASSANDRA:HOSTS"].Split(","))
                                .Build();

            _session = await cluster.ConnectAsync();
            if (keyspace != null)
            {
                _session.CreateKeyspaceIfNotExists(keyspace);
                _session.ChangeKeyspace(keyspace);
                var table = GetFlipsTable(_session);
                await table.CreateIfNotExistsAsync();
            }
            return _session;
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to connect to cassandra");
            throw e;
        }
        finally
        {
            sessionOpenLock.Release();
        }
    }

    public async Task SaveFlip(PastFlip flip)
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        try
        {
            await table.Insert(flip).ExecuteAsync();
        }
        catch (Cassandra.InvalidQueryException e)
        {
            if (e.Message.Contains("No keyspace has been specified"))
            {
                session.ChangeKeyspace(config["CASSANDRA:KEYSPACE"]);
            }
            throw e;
        }
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
            .ClusteringKey(c => c.SellTime)
            .ClusteringKey(c => c.Uid)
            .Column(c => c.FinderType, cm => cm.WithDbType<int>())
            .Column(c => c.ItemTier, cm => cm.WithDbType<int>())
            .Column(c => c.ProfitChanges, cm => cm.Ignore())
            .Column(o => o.Flags, c => c.WithName("flags").WithDbType<int>())
            ), "flips");
    }

    /// <summary>
    /// Brings the db into the current state
    /// </summary>
    /// <returns></returns>
    public async Task Migrate()
    {
        var session = await GetSession();
        var table = GetFlipsTable(session);
        await table.CreateIfNotExistsAsync();
        try
        {
            session.Execute("ALTER TABLE flips ADD flags int;");
            await SaveFlip(new PastFlip() { Flipper = Guid.Empty, SellTime = DateTime.UtcNow, Uid = 0, Flags = 0 });
            logger.LogInformation("inserted flags column");
        }
        catch (Exception e)
        {
            logger.LogInformation(e, "flags exists already");
        }
    }
}
